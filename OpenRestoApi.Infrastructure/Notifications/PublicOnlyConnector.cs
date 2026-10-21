using System.Net;
using System.Net.Sockets;
using CustomAccessibility.Attributes;
using OpenRestoApi.Core.Application.Utilities;

namespace OpenRestoApi.Infrastructure.Notifications;

/// <summary>
/// The connect step for every client that fetches a URL someone else supplied: the Web Push
/// client (an endpoint comes from a browser, or anyone holding a booking or waitlist reference,
/// and <see cref="PushEndpointValidator"/> can only judge its text) and the native-app
/// <c>/.well-known/</c> probe (the address is writable with <c>brand:write</c>). A host name
/// that resolves inside the deployment passes a text check, so the name is resolved here,
/// refused when any address it lands on is private, and the socket is opened to exactly those
/// addresses, which leaves no second lookup for a rebinding name to answer differently. Resolving
/// anywhere else first would reintroduce that second lookup.
/// </summary>
/// <seealso>PublicOnlyConnectorTests.Send_RefusesAHostThatResolvesToAPrivateAddress</seealso>
/// <seealso>PublicOnlyConnectorTests.Send_RefusesAPrivateLiteralWithoutResolving</seealso>
/// <seealso>PublicOnlyConnectorTests.Send_ResolvesTheHostOncePerConnection</seealso>
/// <seealso>PublicOnlyConnectorTests.CreateHandler_UsesNoProxyAndFollowsNoRedirect</seealso>
[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Infrastructure.NativeClients.WellKnownProbe")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.PublicOnlyConnectorTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.WellKnownProbeTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.WebPushResponseLimitTests")]
[ExternalAccessAllowed]
internal sealed class PublicOnlyConnector(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
{
    public const string HttpClientName = "web-push";

    public const string PrivateAddressError = "the address resolves to a private network";

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve =
        resolve ?? Dns.GetHostAddressesAsync;

    /// <summary>
    /// No proxy, since the check would then judge the proxy rather than the endpoint, and no
    /// redirects, since a redirect is how a public host would hand the caller an internal address
    /// (a push service answers rather than forwards).
    /// </summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = ConnectAsync,
        UseProxy = false,
        AllowAutoRedirect = false,
    };

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim('[', ']');
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await _resolve(host, cancellationToken);

        if (addresses.Length == 0 || addresses.Any(address => !PublicAddress.IsPublic(address)))
        {
            throw new HttpRequestException(PrivateAddressError);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
