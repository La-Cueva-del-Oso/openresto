using CustomAccessibility.Attributes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.AdminCredentialRepositoryTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AdminCredentialConcurrencyTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AuthServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.SecurityQuestionsServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.UserServiceTests")]
[ExternalAccessAllowed]
internal class AdminCredentialRepository(AppDbContext db) : IAdminCredentialRepository
{
    private readonly AppDbContext _db = db;

    public async Task<AdminCredential?> GetByIdAsync(int id)
    {
        return await _db.AdminCredentials.FirstOrDefaultAsync(c => c.Id == id);
    }

    public async Task<List<AdminCredential>> GetAllAsync()
    {
        return await _db.AdminCredentials.OrderBy(c => c.Id).ToListAsync();
    }

    public async Task<bool> TrySetRoleAsync(AdminCredential user, string role)
    {
        int rows = await UnlessLastActiveOwner(user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Role, role));
        return await RefreshAsync(user, rows);
    }

    public async Task<bool> TryDeactivateAsync(AdminCredential user)
    {
        int rows = await UnlessLastActiveOwner(user.Id).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(c => c.IsActive, false);
            RevokeSessions(s);
        });
        return await RefreshAsync(user, rows);
    }

    public async Task<bool> TryCountPvqAttemptAsync(int id, DateTime nowUtc, int maxAttempts, DateTime lockedUntilUtc)
    {
        int rows = await _db.AdminCredentials
            .Where(c => c.Id == id && (c.PvqLockedUntil == null || c.PvqLockedUntil <= nowUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PvqLockedUntil, c => c.PvqFailedAttempts + 1 >= maxAttempts ? lockedUntilUtc : c.PvqLockedUntil)
                .SetProperty(c => c.PvqFailedAttempts, c => c.PvqFailedAttempts + 1 >= maxAttempts ? 0 : c.PvqFailedAttempts + 1));
        return rows > 0;
    }

    public async Task CompletePvqVerificationAsync(int id, string resetToken, DateTime resetTokenExpiryUtc)
    {
        await _db.AdminCredentials.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.PvqFailedAttempts, 0)
            .SetProperty(c => c.PvqLockedUntil, (DateTime?)null)
            .SetProperty(c => c.ResetToken, resetToken)
            .SetProperty(c => c.ResetTokenExpiry, resetTokenExpiryUtc));
    }

    public async Task RevokeSessionsAsync(AdminCredential credential)
    {
        // The execution strategy has to own the transaction: it retries the whole unit on a busy database.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync();
            await _db.SaveChangesAsync();
            await _db.AdminCredentials.Where(c => c.Id == credential.Id).ExecuteUpdateAsync(RevokeSessions);
            await _db.Entry(credential).ReloadAsync();
            await tx.CommitAsync();
        });
    }

    // Only an active Owner counts, so deactivating one and then demoting it is not a way around
    // the rule.
    private IQueryable<AdminCredential> UnlessLastActiveOwner(int id)
        => _db.AdminCredentials.Where(c => c.Id == id
            && (c.Role != UserRoles.Owner
                || !c.IsActive
                || _db.AdminCredentials.Any(o => o.Id != id && o.IsActive && o.Role == UserRoles.Owner)));

    private static void RevokeSessions(UpdateSettersBuilder<AdminCredential> s)
    {
        s.SetProperty(c => c.SessionVersion, c => c.SessionVersion + 1);
        s.SetProperty(c => c.ResetToken, (string?)null);
        s.SetProperty(c => c.ResetTokenExpiry, (DateTime?)null);
    }

    // ExecuteUpdate bypasses the change tracker, so the entity the caller holds is brought in line.
    private async Task<bool> RefreshAsync(AdminCredential user, int rowsUpdated)
    {
        if (rowsUpdated == 0)
        {
            return false;
        }

        await _db.Entry(user).ReloadAsync();
        return true;
    }

    public async Task<AdminCredential?> GetByEmailAsync(string email)
    {
        string normalized = email.Trim().ToLowerInvariant();
#pragma warning disable CA1862, CA1311, CA1304 // ToLower in LINQ-to-EF is intentional (ToLowerInvariant is not translatable)
        return await _db.AdminCredentials.FirstOrDefaultAsync(c => c.Email.ToLower() == normalized);
#pragma warning restore CA1862, CA1311, CA1304
    }

    public async Task<AdminCredential?> GetByResetTokenAsync(string resetToken)
    {
        return await _db.AdminCredentials.FirstOrDefaultAsync(c => c.ResetToken == resetToken);
    }

    public async Task<AdminCredential> AddAsync(AdminCredential credential)
    {
        _db.AdminCredentials.Add(credential);
        await _db.SaveChangesAsync();
        return credential;
    }

    public async Task SaveChangesAsync()
    {
        await _db.SaveChangesAsync();
    }
}
