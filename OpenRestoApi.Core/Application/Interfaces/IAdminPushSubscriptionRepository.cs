using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Interfaces;

/// <summary>
/// Persistence of <see cref="AdminPushSubscription"/> (browser Web Push endpoints).
/// Per-endpoint dedup is enforced by a unique index in <c>AppDbContext</c>.
/// </summary>
public interface IAdminPushSubscriptionRepository
{
    /// <summary>Finds the subscription for this endpoint, if any.</summary>
    Task<AdminPushSubscription?> GetByEndpointAsync(string endpoint);

    /// <summary>
    /// Every subscription whose owner is an active account (the push fan-out — subscriptions
    /// are not per-restaurant, but they are per-owner).
    /// </summary>
    Task<List<AdminPushSubscription>> GetForActiveUsersAsync();

    /// <summary>
    /// Registers the endpoint for <c>subscription.UserId</c>, taking it over (and refreshing its keys)
    /// from whoever held it. False, with nothing written, when that account is no longer active at
    /// <paramref name="sessionVersion"/> — its sessions ended while the request was in flight.
    /// </summary>
    Task<bool> TryRegisterAsync(AdminPushSubscription subscription, int sessionVersion);

    /// <summary>Removes the given subscriptions (caller is responsible for SaveChanges — used by SendPushAsync stale cleanup).</summary>
    void RemoveRange(IEnumerable<AdminPushSubscription> subscriptions);

    /// <summary>Flushes pending changes on the underlying DbContext.</summary>
    Task SaveChangesAsync();
}
