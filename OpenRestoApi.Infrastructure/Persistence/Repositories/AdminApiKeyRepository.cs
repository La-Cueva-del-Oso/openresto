using CustomAccessibility.Attributes;
using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.ApiKeyServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.ApiKeyAuthenticationHandlerTests")]
[ExternalAccessAllowed]
internal class AdminApiKeyRepository(AppDbContext db) : IAdminApiKeyRepository
{
    private readonly AppDbContext _db = db;

    public async Task<AdminApiKey?> GetByIdAsync(int id)
    {
        return await _db.AdminApiKeys.FirstOrDefaultAsync(k => k.Id == id);
    }

    public async Task<List<AdminApiKey>> GetByUserIdAsync(int userId)
    {
        return await _db.AdminApiKeys
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ThenByDescending(k => k.Id)
            .ToListAsync();
    }

    // The account is checked after the insert has committed: a revocation commits either before the
    // row exists (the check sees it and the row goes) or after (the revocation sweeps the row).
    public async Task<bool> TryAddAsync(AdminApiKey key, int sessionVersion)
    {
        _db.AdminApiKeys.Add(key);
        await _db.SaveChangesAsync();

        bool accountStillAsRead = await _db.AdminCredentials
            .AnyAsync(c => c.Id == key.UserId && c.SessionVersion == sessionVersion && c.IsActive);
        if (!accountStillAsRead)
        {
            _db.AdminApiKeys.Remove(key);
            await _db.SaveChangesAsync();
        }

        return accountStillAsRead;
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
