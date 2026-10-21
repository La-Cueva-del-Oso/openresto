using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Core.Domain;
using WebPush;

namespace OpenRestoApi.Core.Application.Services;

/// <summary>
/// Routes a guest reminder by channel: browsers through the same VAPID Web Push client the
/// admin notifications use, the native app through Expo's push service. The Web Push payload
/// carries the same <c>title</c>/<c>body</c> shape as the admin payload so <c>sw.js</c> shows
/// both with one handler; <c>url</c> is what its click opens and <c>tag</c> what it replaces.
/// </summary>
/// <seealso>GuestPushSenderTests.SendAsync_WebPush_ReportsStaleOnGoneOrNotFound</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_WebPush_ReportsFailureWithoutVapid</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_WebPush_ReportsAnUnreachableAddressAsAFailure</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_Expo_DelegatesToTheExpoClient</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_WebPush_ReportsAClientThatNeverAnswersAsAFailure</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_Expo_ReportsAClientThatNeverAnswersAsAFailure</seealso>
/// <seealso>GuestPushSenderTests.SendAsync_WebPush_LetsTheCallersOwnCancellationThrough</seealso>
public sealed class GuestPushSender(
    IWebPushClient webPushClient,
    IExpoPushClient expoPushClient,
    IOptions<VapidSettings> vapidOptions,
    IOptions<GuestPushSettings> pushOptions) : IGuestPushSender
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly VapidSettings _vapid = vapidOptions.Value;
    private readonly TimeSpan _sendTimeout = pushOptions.Value.SendTimeout;

    /// <summary>
    /// A send that outlasts <see cref="GuestPushSettings.SendTimeout"/> is that subscription's
    /// failure, so one unresponsive push service cannot hold up the reminder pass; a cancellation
    /// requested by the caller still propagates.
    /// </summary>
    public async Task<GuestPushResult> SendAsync(GuestPushAddress address, GuestPushMessage message, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_sendTimeout);

        try
        {
            return await (address.Channel switch
            {
                GuestPushChannels.Expo => expoPushClient.SendAsync(address.Endpoint, message, timeout.Token),
                GuestPushChannels.WebPush => SendWebPushAsync(address, message, timeout.Token),
                _ => Task.FromResult(GuestPushResult.Failed($"Unknown channel '{address.Channel}'")),
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return GuestPushResult.Failed("The push service did not answer in time");
        }
    }

    private async Task<GuestPushResult> SendWebPushAsync(GuestPushAddress address, GuestPushMessage message, CancellationToken cancellationToken)
    {
        if (!_vapid.IsConfigured)
        {
            return GuestPushResult.Failed("VAPID is not configured");
        }

        var payload = new
        {
            message.Title,
            message.Body,
            message.Url,
            message.Tag,
        };
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        var vapidDetails = new VapidDetails(_vapid.Subject, _vapid.PublicKey, _vapid.PrivateKey);
        var pushSub = new PushSubscription(address.Endpoint, address.P256dh, address.Auth);

        try
        {
            await webPushClient.SendNotificationAsync(pushSub, json, vapidDetails, cancellationToken);
            return GuestPushResult.Delivered;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            return GuestPushResult.Stale;
        }
        catch (WebPushException ex)
        {
            return GuestPushResult.Failed($"HTTP {(int)ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // As in BookingNotificationService: an address that cannot be reached, or is refused
            // for resolving to a private network, is this subscription's failure, not an escape
            // that aborts the reminder run for every guest after it.
            return GuestPushResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
