using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Moq;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Extensions;
using OpenRestoApi.Infrastructure.Notifications;
using OpenRestoApi.Tests.Integration;
using WebPush;

namespace OpenRestoApi.Tests.Infrastructure;

/// <summary>
/// The Web Push client posts to an address a guest chose and, on a refusal, reads the whole body
/// of the answer, so the client the app registers caps what it will buffer. These run the real
/// library over that registered configuration (a stub in place of the socket) and pin that an
/// oversized answer is one subscription's failure while an ordinary one is not touched.
/// </summary>
public class WebPushResponseLimitTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private const string Endpoint = "https://push.example/sub1";

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static GuestPushAddress NewSubscription()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        ECPoint q = key.ExportParameters(false).Q;
        byte[] publicKey = [0x04, .. q.X!, .. q.Y!];
        return new GuestPushAddress(GuestPushChannels.WebPush, Endpoint, Base64Url(publicKey), Base64Url(RandomNumberGenerator.GetBytes(16)));
    }

    private GuestPushSender SenderAnsweringWith(HttpStatusCode status, string body)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(PublicOnlyConnector.HttpClientName);
        var client = new HttpClient(new StubHandler(status, body));
        foreach (Action<HttpClient> configure in options.HttpClientActions)
        {
            configure(client);
        }

        VapidDetails vapid = VapidHelper.GenerateVapidKeys();
        var settings = Options.Create(new VapidSettings
        {
            Subject = "mailto:ops@openresto.com",
            PublicKey = vapid.PublicKey,
            PrivateKey = vapid.PrivateKey,
        });
        return new GuestPushSender(new WebPushClient(client), new Mock<IExpoPushClient>().Object, settings, Options.Create(new GuestPushSettings()));
    }

    private static readonly GuestPushMessage Message = new("Your table", "Tomorrow at 19:30", "https://bookings.example.com/x", "booking-7");

    [Fact]
    public async Task SendAsync_ReportsAnAnswerOverTheCapAsThatSubscriptionsFailure()
    {
        string body = new('x', ServiceCollectionExtensions.WebPushMaxResponseBytes + 1);

        GuestPushResult result = await SenderAnsweringWith(HttpStatusCode.Created, body)
            .SendAsync(NewSubscription(), Message);

        Assert.Equal(GuestPushOutcome.Failed, result.Outcome);
        Assert.StartsWith(nameof(HttpRequestException), result.Error);
    }

    [Fact]
    public async Task SendAsync_AcceptsAnAnswerAtTheCap()
    {
        string body = new('x', ServiceCollectionExtensions.WebPushMaxResponseBytes);

        GuestPushResult result = await SenderAnsweringWith(HttpStatusCode.InternalServerError, body)
            .SendAsync(NewSubscription(), Message);

        Assert.Equal(GuestPushOutcome.Failed, result.Outcome);
        Assert.StartsWith("HTTP 500:", result.Error);
    }

    [Fact]
    public async Task SendAsync_DeliversWhenThePushServiceAccepts()
    {
        GuestPushResult result = await SenderAnsweringWith(HttpStatusCode.Created, "")
            .SendAsync(NewSubscription(), Message);

        Assert.Equal(GuestPushOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public async Task SendAsync_StillReportsAGoneAddressAsStale_WithASmallBody()
    {
        GuestPushResult result = await SenderAnsweringWith(HttpStatusCode.Gone, "expired")
            .SendAsync(NewSubscription(), Message);

        Assert.Equal(GuestPushOutcome.Stale, result.Outcome);
    }
}
