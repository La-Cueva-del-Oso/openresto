using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Infrastructure.NativeClients;
using OpenRestoApi.Infrastructure.Notifications;
using OpenRestoApi.Tests.Integration;

namespace OpenRestoApi.Tests.Infrastructure;

/// <summary>
/// The fetching half of the readiness checks. The URL it is handed was composed from an
/// admin-writable address, so what this pins is that a name resolving inside the deployment is
/// refused at connection time, by the connector the client is registered with (a lookup of the
/// probe's own beforehand is what a rebinding name answers differently), and that a public one
/// is fetched and reported as a status and a content type.
/// </summary>
public class WellKnownProbeTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private sealed class StubHandler(HttpStatusCode status, string contentType, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    private sealed class StubFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public StubFactory(HttpMessageHandler handler) : this(() => new HttpClient(handler, disposeHandler: false)) { }

        public HttpClient CreateClient(string name) => create();
    }

    /// <summary>The same handler production registers, with DNS stubbed.</summary>
    private static StubFactory ConnectorFactory(Func<string, CancellationToken, Task<IPAddress[]>> resolve)
        => new(() => new HttpClient(new PublicOnlyConnector(resolve).CreateHandler()));

    private static Task<IPAddress[]> ResolvesTo(params string[] addresses)
        => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray());

    private static readonly Uri AssetLinks = new("https://bookings.example.com/.well-known/assetlinks.json");

    [Theory]
    [InlineData("10.0.0.12")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task FetchAsync_RefusesAHostThatResolvesToAPrivateAddress(string resolved)
    {
        var probe = new WellKnownProbe(ConnectorFactory((_, _) => ResolvesTo(resolved)));

        WellKnownProbeResult result = await probe.FetchAsync(AssetLinks);

        Assert.Equal(WellKnownProbe.PrivateAddressError, result.Error);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task FetchAsync_RefusesAHostThatResolvesToAMixOfPublicAndPrivateAddresses()
    {
        var probe = new WellKnownProbe(ConnectorFactory((_, _) => ResolvesTo("203.0.113.9", "10.0.0.12")));

        WellKnownProbeResult result = await probe.FetchAsync(AssetLinks);

        Assert.Equal(WellKnownProbe.PrivateAddressError, result.Error);
    }

    [Theory]
    [InlineData("https://192.168.1.10/.well-known/assetlinks.json")]
    [InlineData("https://[::1]/.well-known/assetlinks.json")]
    [InlineData("https://localhost/.well-known/assetlinks.json")]
    public async Task FetchAsync_RefusesAPrivateLiteralWithoutResolving(string url)
    {
        bool resolved = false;
        var probe = new WellKnownProbe(ConnectorFactory((_, _) => { resolved = true; return ResolvesTo("8.8.8.8"); }));

        WellKnownProbeResult result = await probe.FetchAsync(new Uri(url));

        Assert.Equal(WellKnownProbe.PrivateAddressError, result.Error);
        Assert.False(resolved);
    }

    [Fact]
    public async Task FetchAsync_ReportsTheStatusAndContentTypeOfAPublicHost()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "application/json", """{"applinks":{}}""");
        var probe = new WellKnownProbe(new StubFactory(handler));

        WellKnownProbeResult result = await probe.FetchAsync(new Uri("https://bookings.example.com/.well-known/apple-app-site-association"));

        Assert.Null(result.Error);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal("""{"applinks":{}}""", result.Body);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task FetchAsync_ReportsAFailedLookupByTypeNotMessage()
    {
        var probe = new WellKnownProbe(ConnectorFactory(
            (_, _) => throw new SocketException((int)SocketError.HostNotFound)));

        WellKnownProbeResult result = await probe.FetchAsync(new Uri("https://nowhere.example.com/.well-known/assetlinks.json"));

        Assert.Equal(nameof(HttpRequestException), result.Error);
    }

    /// <summary>
    /// The handler the app registers for the probe's client, built the way the client factory
    /// builds it and pointed at a loopback listener past the probe's own literal check: a plain
    /// handler would connect, so that nothing arrives is the connector wired in.
    /// </summary>
    [Fact]
    public async Task TheRegisteredClientRefusesALoopbackAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            IServiceProvider services = factory.Services;
            HttpMessageHandlerBuilder builder = services.GetRequiredService<HttpMessageHandlerBuilder>();
            foreach (Action<HttpMessageHandlerBuilder> configure in services
                .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get(WellKnownProbe.HttpClientName).HttpMessageHandlerBuilderActions)
            {
                configure(builder);
            }

            using var client = new HttpClient(builder.Build());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.GetAsync($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/", timeout.Token));

            Assert.StartsWith(WellKnownProbe.PrivateAddressError, ex.Message);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The container has nothing registered beyond the client factory and must still build the
    /// probe — which nothing else would catch until the status endpoint was hit.
    /// </summary>
    [Fact]
    public void ResolvesFromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WellKnownProbe.HttpClientName);
        services.AddScoped<IWellKnownProbe, WellKnownProbe>();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        Assert.IsType<WellKnownProbe>(scope.ServiceProvider.GetRequiredService<IWellKnownProbe>());
    }
}
