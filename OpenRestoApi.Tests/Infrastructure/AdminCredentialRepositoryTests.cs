using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Infrastructure;

/// <summary>
/// Real SQLite rather than the in-memory provider: revoking sessions raises the version in SQL,
/// which the in-memory provider cannot run.
/// </summary>
public class AdminCredentialRepositoryTests
{
    private static AdminCredential Seed(AppDbContext db)
    {
        var cred = new AdminCredential
        {
            Email = "owner@example.com",
            PasswordHash = "old-hash",
            PasswordSalt = "old-salt",
            PvqQuestion = "Q",
            ResetToken = "T",
            ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15),
        };
        db.AdminCredentials.Add(cred);
        db.SaveChanges();
        return cred;
    }

    [Fact]
    public async Task RevokeSessionsAsync_Raises_The_Version_By_One_And_Drops_A_Pending_Reset_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(RevokeSessionsAsync_Raises_The_Version_By_One_And_Drops_A_Pending_Reset_Token));
        AdminCredential cred = Seed(db);

        await new AdminCredentialRepository(db).RevokeSessionsAsync(cred);

        AdminCredential stored = await db.AdminCredentials.AsNoTracking().SingleAsync();
        Assert.Equal(1, stored.SessionVersion);
        Assert.Null(stored.ResetToken);
        Assert.Null(stored.ResetTokenExpiry);
        // The security question is a standing credential, not a pending one: signing out keeps it.
        Assert.Equal("Q", stored.PvqQuestion);
        Assert.Equal(1, cred.SessionVersion);
        Assert.Null(cred.ResetToken);
    }

    [Fact]
    public async Task RevokeSessionsAsync_Saves_The_Pending_Changes_With_The_Revocation()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(RevokeSessionsAsync_Saves_The_Pending_Changes_With_The_Revocation));
        AdminCredential cred = Seed(db);
        cred.PasswordHash = "new-hash";

        await new AdminCredentialRepository(db).RevokeSessionsAsync(cred);

        AdminCredential stored = await db.AdminCredentials.AsNoTracking().SingleAsync();
        Assert.Equal("new-hash", stored.PasswordHash);
        Assert.Equal(1, stored.SessionVersion);
        Assert.Equal("new-hash", cred.PasswordHash);
    }
}
