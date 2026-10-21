using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Services;

public class NotificationService(
    IAdminNotificationRepository notificationRepository,
    IAdminPushSubscriptionRepository pushSubscriptionRepository,
    IOptions<VapidSettings> vapidOptions,
    ILogger<NotificationService> logger,
    IAuditScope? audit = null) : INotificationService
{
    private readonly IAdminNotificationRepository _notificationRepository = notificationRepository;
    private readonly IAdminPushSubscriptionRepository _pushSubscriptionRepository = pushSubscriptionRepository;
    private readonly VapidSettings _vapid = vapidOptions.Value;
    private readonly ILogger<NotificationService> _log = logger;
    private readonly IAuditScope _audit = audit ?? NullAuditScope.Instance;

    public string? GetVapidPublicKey() =>
        _vapid.IsConfigured ? _vapid.PublicKey : null;

    // ── Query ─────────────────────────────────────────────────────────────────

    public async Task<(List<AdminNotificationDto> Items, int TotalCount)> GetNotificationsAsync(
        int? restaurantId, string? type, bool? unreadOnly, int page, int pageSize)
    {
        (List<AdminNotification> items, int total) = await _notificationRepository.QueryPagedAsync(restaurantId, type, unreadOnly, page, pageSize);
        return (items.Select(ToDto).ToList(), total);
    }

    public async Task<int> GetUnreadCountAsync(int? restaurantId) =>
        await _notificationRepository.CountUnreadAsync(restaurantId);

    public async Task MarkReadAsync(int notificationId) =>
        await _notificationRepository.MarkReadAsync(notificationId);

    public async Task MarkAllReadAsync(int restaurantId) =>
        await _notificationRepository.MarkAllReadAsync(restaurantId);

    // ── Push subscriptions ────────────────────────────────────────────────────

    /// <summary>
    /// <seealso>NotificationServiceTests.SubscribeAsync_RejectsAnEndpointThatIsNotAPublicHttpsUrl</seealso>
    /// </summary>
    /// <seealso>NotificationServiceTests.SubscribeAsync_AssignsTheCallerAsOwner</seealso>
    /// <seealso>NotificationServiceTests.SubscribeAsync_TransfersAnEndpointAnotherUserHeld_ToTheCaller</seealso>
    /// <seealso>NotificationServiceTests.SubscribeAsync_RegistersWhenTheSessionVersionStillMatches</seealso>
    /// <seealso>NotificationServiceTests.SubscribeAsync_RefusesASessionTheAccountHasMovedPast_AndLeavesNoRow</seealso>
    /// <seealso>NotificationServiceTests.SubscribeAsync_RefusesADeactivatedAccount_AndLeavesNoRow</seealso>
    /// <seealso>NotificationServiceTests.SubscribeAsync_FromAnEndedSession_DoesNotTakeOverTheEndpointAnotherAccountHolds</seealso>
    public async Task<bool> SubscribeAsync(int userId, int sessionVersion, PushSubscribeRequest request)
    {
        if (!PushEndpointValidator.IsValidWebPushEndpoint(request.Endpoint))
        {
            throw new ValidationException("The push endpoint must be a public https URL.") { Code = ErrorCodes.NotificationPushEndpointInvalid };
        }

        // One row per endpoint covers every location, so re-subscribing from the same browser
        // refreshes the keys, and a browser another admin used follows whoever subscribes last.
        bool registered = await _pushSubscriptionRepository.TryRegisterAsync(
            new AdminPushSubscription
            {
                UserId = userId,
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                UserAgent = request.UserAgent,
                CreatedAt = DateTime.UtcNow,
            },
            sessionVersion);
        if (!registered)
        {
            _log.LogWarning("[Push] Subscribe refused: the caller's session has ended");
            return false;
        }

        _log.LogInformation("[Push] Subscription registered");

        // No endpoint or key material: a push subscription's keys are credentials.
        // No restaurantId either — the subscription covers every location.
        _audit.Describe(AuditActions.PushSubscribe,
            summary: "Enabled push notifications for a device");
        return true;
    }

    /// <seealso>NotificationServiceTests.UnsubscribeAsync_RemovesTheCallersOwnEndpoint_AndLeavesOtherBrowsersAlone</seealso>
    /// <seealso>NotificationServiceTests.UnsubscribeAsync_LeavesAnotherUsersEndpointAlone</seealso>
    public async Task UnsubscribeAsync(int userId, string endpoint)
    {
        AdminPushSubscription? sub = await _pushSubscriptionRepository.GetByEndpointAsync(endpoint);
        // Another user's endpoint is a silent no-op, the same as an unknown one: the caller
        // learns nothing about who holds it.
        if (sub is not null && sub.UserId == userId)
        {
            _pushSubscriptionRepository.RemoveRange([sub]);
            await _pushSubscriptionRepository.SaveChangesAsync();
            _log.LogInformation("[Push] Unsubscribed subscription id={Id}", sub.Id);
            _audit.Describe(AuditActions.PushUnsubscribe,
                summary: "Disabled push notifications for a device");
        }
        else
        {
            _log.LogWarning("[Push] Unsubscribe called but no matching subscription found");
        }
    }

    public async Task DeleteByIdAsync(int notificationId)
    {
        await _notificationRepository.DeleteByIdAsync(notificationId);
        _audit.Describe(AuditActions.NotificationDelete, AuditTargets.Notification,
            AuditTargets.IdOf(notificationId), summary: "Deleted 1 notification");
    }

    public async Task DeleteByIdsAsync(List<int> notificationIds)
    {
        await _notificationRepository.DeleteByIdsAsync(notificationIds);
        _audit.Describe(AuditActions.NotificationDelete, AuditTargets.Notification,
            summary: $"Deleted {notificationIds.Count} notifications");
    }

    public async Task DeleteAllAsync(int? restaurantId, string? type, bool? unreadOnly)
    {
        await _notificationRepository.DeleteAllAsync(restaurantId, type, unreadOnly);
        _audit.Describe(AuditActions.NotificationDelete, AuditTargets.Notification,
            restaurantId: restaurantId, summary: "Cleared the matching notifications");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static AdminNotificationDto ToDto(AdminNotification n) => new(
        n.Id,
        n.RestaurantId,
        n.RestaurantName,
        n.BookingId,
        n.BookingRef,
        n.Type,
        n.CustomerName,
        n.BookingDate,
        n.Seats,
        n.IsRead,
        n.CreatedAt,
        n.PushSentAt,
        n.PushError
    );
}
