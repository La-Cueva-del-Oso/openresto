using CustomAccessibility.Attributes;
using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.NotificationServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.BookingNotificationServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AdminCredentialConcurrencyTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AdminApiKeyRevocationTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AuthServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.UserServiceTests")]
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

    public async Task DeleteForUserAsync(int userId)
    {
        List<AdminPushSubscription> owned = await _db.AdminPushSubscriptions.Where(s => s.UserId == userId).ToListAsync();
        _db.AdminPushSubscriptions.RemoveRange(owned);
        await _db.SaveChangesAsync();
    }

    public async Task<AdminPushSubscription> AddAsync(AdminPushSubscription subscription)
    {
        _db.AdminPushSubscriptions.Add(subscription);
        await _db.SaveChangesAsync();
        return subscription;
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
