using CustomAccessibility.Attributes;
using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.NotificationServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.BookingNotificationServiceTests")]
[ExternalAccessAllowed]
internal class AdminPushSubscriptionRepository(AppDbContext db) : IAdminPushSubscriptionRepository
{
    private readonly AppDbContext _db = db;

    public async Task<AdminPushSubscription?> GetByEndpointAsync(string endpoint)
    {
        return await _db.AdminPushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == endpoint);
    }

    public async Task<List<AdminPushSubscription>> GetForActiveUsersAsync()
    {
        return await _db.AdminPushSubscriptions
            .Where(s => s.User!.IsActive)
            .ToListAsync();
    }

    // A password change or reset deletes the account's subscriptions in the same step that raises its
    // SessionVersion, so a registration from the session it ended must not land afterwards. Taking over
    // an existing endpoint is one conditional UPDATE, which either precedes the sweep (and is swept) or
    // follows the revocation (and matches nothing), leaving the previous holder's row untouched. A new
    // row is inserted and then checked, like AdminApiKeyRepository.TryAddAsync: the check sees the
    // revocation (and the row goes) or the revocation sweeps the row.
    public async Task<bool> TryRegisterAsync(AdminPushSubscription subscription, int sessionVersion)
    {
        int userId = subscription.UserId;
        IQueryable<AdminCredential> accountStillAsRead = _db.AdminCredentials
            .Where(c => c.Id == userId && c.SessionVersion == sessionVersion && c.IsActive);

        int taken = await _db.AdminPushSubscriptions
            .Where(s => s.Endpoint == subscription.Endpoint && accountStillAsRead.Any())
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.UserId, userId)
                .SetProperty(x => x.P256dh, subscription.P256dh)
                .SetProperty(x => x.Auth, subscription.Auth)
                .SetProperty(x => x.UserAgent, subscription.UserAgent));
        if (taken > 0)
        {
            return true;
        }

        // The endpoint is held and the account is gone: a refusal, not an insert the index would reject.
        if (await _db.AdminPushSubscriptions.AnyAsync(s => s.Endpoint == subscription.Endpoint))
        {
            return false;
        }

        _db.AdminPushSubscriptions.Add(subscription);
        await _db.SaveChangesAsync();

        if (await accountStillAsRead.AnyAsync())
        {
            return true;
        }

        _db.AdminPushSubscriptions.Remove(subscription);
        await _db.SaveChangesAsync();
        return false;
    }

    public void RemoveRange(IEnumerable<AdminPushSubscription> subscriptions)
    {
        _db.AdminPushSubscriptions.RemoveRange(subscriptions);
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
