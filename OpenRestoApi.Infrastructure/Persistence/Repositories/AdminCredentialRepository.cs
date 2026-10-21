using CustomAccessibility.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.AdminCredentialRepositoryTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AdminApiKeyRevocationTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AdminCredentialConcurrencyTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.AuthServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.SecurityQuestionsServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.UserServiceTests")]
[ExternalAccessAllowed]
internal class AdminCredentialRepository(AppDbContext db, ISystemClock? clock = null) : IAdminCredentialRepository
{
    private const int SqliteUniqueConstraint = 2067; // SQLITE_CONSTRAINT_UNIQUE

    private readonly AppDbContext _db = db;
    private readonly ISystemClock _clock = clock ?? new SystemClock();

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
        int rows = await UpdateAndRevokeApiKeysAsync(UnlessLastActiveOwner(user.Id), user.Id, s =>
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

    public async Task<bool> TryCompletePvqVerificationAsync(AdminCredential verified, string resetToken, DateTime resetTokenExpiryUtc)
    {
        int rows = await StillAsRead(verified)
            .Where(c => c.PvqAnswerHash == verified.PvqAnswerHash)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PvqFailedAttempts, 0)
                .SetProperty(c => c.PvqLockedUntil, (DateTime?)null)
                .SetProperty(c => c.ResetToken, resetToken)
                .SetProperty(c => c.ResetTokenExpiry, resetTokenExpiryUtc));
        return rows > 0;
    }

    public async Task<bool> TryResetPasswordAsync(AdminCredential credential, string resetToken, DateTime nowUtc, string passwordHash, string passwordSalt)
    {
        int rows = await UpdateAndRevokeApiKeysAsync(
            StillAsRead(credential).Where(c => c.ResetToken == resetToken && c.ResetTokenExpiry >= nowUtc),
            credential.Id,
            s => SetPassword(s, passwordHash, passwordSalt));
        return await RefreshAsync(credential, rows);
    }

    public async Task<bool> TryChangePasswordAsync(AdminCredential credential, string passwordHash, string passwordSalt)
    {
        int rows = await UpdateAndRevokeApiKeysAsync(
            StillAsRead(credential), credential.Id, s => SetPassword(s, passwordHash, passwordSalt));
        return await RefreshAsync(credential, rows);
    }

    public async Task<bool> TryChangeEmailAsync(AdminCredential credential, string email)
    {
        int rows;
        try
        {
            rows = await StillAsRead(credential).ExecuteUpdateAsync(s => s.SetProperty(c => c.Email, email));
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return false;
        }

        return await RefreshAsync(credential, rows);
    }

    public async Task<bool> TrySetSecurityQuestionAsync(AdminCredential credential, string question, string answerHash, string answerSalt)
    {
        int rows = await StillAsRead(credential).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.PvqQuestion, question)
            .SetProperty(c => c.PvqAnswerHash, answerHash)
            .SetProperty(c => c.PvqAnswerSalt, answerSalt));
        return await RefreshAsync(credential, rows);
    }

    public Task RevokeSessionsAsync(AdminCredential credential) => RevokeSessionsCoreAsync(credential, revokeApiKeys: false);

    public Task RevokeSessionsAndApiKeysAsync(AdminCredential credential) => RevokeSessionsCoreAsync(credential, revokeApiKeys: true);

    private async Task RevokeSessionsCoreAsync(AdminCredential credential, bool revokeApiKeys)
    {
        // The execution strategy has to own the transaction: it retries the whole unit on a busy database.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync();
            // Changes stay pending until the commit: a retry after a rollback must find them still there.
            await _db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
            await _db.AdminCredentials.Where(c => c.Id == credential.Id).ExecuteUpdateAsync(RevokeSessions);
            if (revokeApiKeys)
            {
                await RevokeApiKeysAsync(credential.Id);
            }

            await _db.Entry(credential).ReloadAsync();
            await tx.CommitAsync();
        });
        _db.ChangeTracker.AcceptAllChanges();
    }

    // The credential write and the key revocation land together or not at all, so no instant exists
    // where the sessions are dead and a key the previous holder minted still works. Nothing is
    // revoked when the write is refused.
    private async Task<int> UpdateAndRevokeApiKeysAsync(
        IQueryable<AdminCredential> target, int userId, Action<UpdateSettersBuilder<AdminCredential>> set)
    {
        int rows = 0;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync();
            rows = await target.ExecuteUpdateAsync(set);
            if (rows > 0)
            {
                await RevokeApiKeysAsync(userId);
            }

            await tx.CommitAsync();
        });
        return rows;
    }

    // Already-revoked keys keep their original RevokedAt.
    private Task<int> RevokeApiKeysAsync(int userId)
    {
        DateTime now = _clock.UtcNow;
        return _db.AdminApiKeys
            .Where(k => k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now));
    }

    private static void SetPassword(UpdateSettersBuilder<AdminCredential> s, string hash, string salt)
    {
        s.SetProperty(c => c.PasswordHash, hash);
        s.SetProperty(c => c.PasswordSalt, salt);
        RevokeSessions(s);
    }

    private static bool IsUniqueViolation(Exception ex)
        => ex is DbUpdateException { InnerException: SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraint } }
            or SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraint };

    // Only an active Owner counts, so deactivating one and then demoting it is not a way around
    // the rule.
    private IQueryable<AdminCredential> UnlessLastActiveOwner(int id)
        => _db.AdminCredentials.Where(c => c.Id == id
            && (c.Role != UserRoles.Owner
                || !c.IsActive
                || _db.AdminCredentials.Any(o => o.Id != id && o.IsActive && o.Role == UserRoles.Owner)));

    // Every revocation (password change or reset, deactivation, sign-out) raises SessionVersion, so
    // a write held to the version it read is refused once any of them has landed in between.
    private IQueryable<AdminCredential> StillAsRead(AdminCredential read)
        => _db.AdminCredentials.Where(c => c.Id == read.Id && c.SessionVersion == read.SessionVersion && c.IsActive);

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
