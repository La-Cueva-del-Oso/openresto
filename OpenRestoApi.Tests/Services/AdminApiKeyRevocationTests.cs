using Microsoft.EntityFrameworkCore;
using Moq;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// A key minted by whoever held a compromised account must not survive the account's recovery.
/// Every scenario has three keys: a live one on the account being recovered, one that account had
/// already revoked, and a live one on a bystander. Recovery revokes the first, leaves the second
/// at its original timestamp and never reaches the third. Real SQLite, since the revocation is an
/// <c>ExecuteUpdate</c> inside a transaction.
/// </summary>
public sealed class AdminApiKeyRevocationTests : IDisposable
{
    private const string Password = "Current123!";
    private static readonly DateTime EarlierRevocation = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly AppDbContext _db = TestDbFactory.CreateSqlite($"keyrev-{Guid.NewGuid():N}");
    private readonly AdminCredentialRepository _repository;
    private readonly PasswordService _passwords = new();
    private int _keySeq;

    public AdminApiKeyRevocationTests() => _repository = new AdminCredentialRepository(_db);

    public void Dispose() => _db.Dispose();

    private async Task<AdminCredential> SeedAccountAsync(string email, string role = UserRoles.Manager)
    {
        (string hash, string salt) = _passwords.Hash(Password);
        var account = new AdminCredential { Email = email, PasswordHash = hash, PasswordSalt = salt, Role = role };
        _db.AdminCredentials.Add(account);
        await _db.SaveChangesAsync();
        return account;
    }

    private async Task SeedKeyAsync(AdminCredential owner, string name, DateTime? revokedAt = null)
    {
        _db.AdminApiKeys.Add(new AdminApiKey
        {
            UserId = owner.Id,
            Name = name,
            KeyHash = $"hash-{_keySeq++}",
            Prefix = "orst_x",
            ScopesJson = "[]",
            RevokedAt = revokedAt,
        });
        await _db.SaveChangesAsync();
    }

    private sealed record Scenario(AdminCredential Victim, AdminCredential Caller, DateTime StartedAt);

    private async Task<Scenario> ArrangeAsync()
    {
        AdminCredential victim = await SeedAccountAsync("victim@example.com");
        AdminCredential bystander = await SeedAccountAsync("bystander@example.com");
        AdminCredential caller = await SeedAccountAsync("caller@example.com", UserRoles.Owner);
        await SeedKeyAsync(victim, "live");
        await SeedKeyAsync(victim, "already-revoked", EarlierRevocation);
        await SeedKeyAsync(bystander, "bystander");
        return new Scenario(victim, caller, DateTime.UtcNow.AddSeconds(-1));
    }

    private async Task<AdminCredential> StoredAsync(int id)
        => await _db.AdminCredentials.AsNoTracking().SingleAsync(c => c.Id == id);

    private async Task<Dictionary<string, DateTime?>> RevokedAtByNameAsync()
        => await _db.AdminApiKeys.AsNoTracking().ToDictionaryAsync(k => k.Name, k => k.RevokedAt);

    private async Task AssertOnlyTheVictimsLiveKeyWasRevokedAsync(Scenario s)
    {
        Dictionary<string, DateTime?> keys = await RevokedAtByNameAsync();
        Assert.True(keys["live"] >= s.StartedAt);
        Assert.Equal(EarlierRevocation, keys["already-revoked"]);
        Assert.Null(keys["bystander"]);
    }

    private async Task AssertNoKeyWasTouchedAsync()
    {
        Dictionary<string, DateTime?> keys = await RevokedAtByNameAsync();
        Assert.Null(keys["live"]);
        Assert.Equal(EarlierRevocation, keys["already-revoked"]);
        Assert.Null(keys["bystander"]);
    }

    private AuthService AuthAs(AdminCredential caller)
    {
        var jwt = new Mock<IJwtTokenService>();
        jwt.Setup(j => j.Generate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).Returns("token");
        return new AuthService(_repository, _passwords, jwt.Object, FakeCurrentUser.For(caller), new AdminPushSubscriptionRepository(_db));
    }

    private UserService UsersAs(AdminCredential caller)
        => new(_repository, _passwords, FakeCurrentUser.For(caller), new AdminPushSubscriptionRepository(_db));

    private async Task SetResetTokenAsync(int id, string token)
        => await _db.AdminCredentials.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.ResetToken, token)
            .SetProperty(c => c.ResetTokenExpiry, DateTime.UtcNow.AddMinutes(15)));

    // ── Revoked on recovery ─────────────────────────────────────────────────────

    [Fact]
    public async Task Changing_The_Password_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those()
    {
        Scenario s = await ArrangeAsync();

        string? token = await AuthAs(s.Victim).ChangePasswordAsync(Password, "Changed123!");

        Assert.NotNull(token);
        await AssertOnlyTheVictimsLiveKeyWasRevokedAsync(s);
    }

    [Fact]
    public async Task Resetting_The_Password_With_A_Token_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those()
    {
        Scenario s = await ArrangeAsync();
        await SetResetTokenAsync(s.Victim.Id, "T");

        bool reset = await AuthAs(s.Victim).ResetPasswordAsync("T", "Changed123!");

        Assert.True(reset);
        await AssertOnlyTheVictimsLiveKeyWasRevokedAsync(s);
    }

    [Fact]
    public async Task An_Owner_Reset_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those()
    {
        Scenario s = await ArrangeAsync();

        await UsersAs(s.Caller).ResetPasswordAsync(s.Victim.Id, new ResetUserPasswordRequest { NewPassword = "OwnerReset123!" });

        await AssertOnlyTheVictimsLiveKeyWasRevokedAsync(s);
    }

    [Fact]
    public async Task Deactivating_An_Account_Revokes_Its_Live_Api_Keys_And_Only_Those()
    {
        Scenario s = await ArrangeAsync();

        await UsersAs(s.Caller).SetActiveAsync(s.Victim.Id, new SetUserActiveRequest { IsActive = false });

        await AssertOnlyTheVictimsLiveKeyWasRevokedAsync(s);
    }

    // ── Not revoked ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Signing_Out_Leaves_The_Accounts_Api_Keys_Alone()
    {
        Scenario s = await ArrangeAsync();

        await AuthAs(s.Victim).LogoutAsync();

        await AssertNoKeyWasTouchedAsync();
        Assert.Equal(1, (await StoredAsync(s.Victim.Id)).SessionVersion);
    }

    [Fact]
    public async Task Reactivating_An_Account_Does_Not_Revive_Its_Revoked_Api_Keys()
    {
        Scenario s = await ArrangeAsync();
        UserService users = UsersAs(s.Caller);
        await users.SetActiveAsync(s.Victim.Id, new SetUserActiveRequest { IsActive = false });

        await users.SetActiveAsync(s.Victim.Id, new SetUserActiveRequest { IsActive = true });

        Assert.True((await StoredAsync(s.Victim.Id)).IsActive);
        await AssertOnlyTheVictimsLiveKeyWasRevokedAsync(s);
    }

    [Fact]
    public async Task A_Refused_Password_Change_Leaves_The_Accounts_Api_Keys_Alone()
    {
        Scenario s = await ArrangeAsync();
        AdminCredential staleRead = (await _repository.GetByIdAsync(s.Victim.Id))!;
        await _db.AdminCredentials.Where(c => c.Id == s.Victim.Id)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.SessionVersion, x => x.SessionVersion + 1));

        bool changed = await _repository.TryChangePasswordAsync(staleRead, "h", "s");

        Assert.False(changed);
        await AssertNoKeyWasTouchedAsync();
    }

    [Fact]
    public async Task A_Refused_Token_Reset_Leaves_The_Accounts_Api_Keys_Alone()
    {
        Scenario s = await ArrangeAsync();
        await SetResetTokenAsync(s.Victim.Id, "T");
        AdminCredential read = await StoredAsync(s.Victim.Id);

        bool reset = await _repository.TryResetPasswordAsync(read, "not-the-token", DateTime.UtcNow, "h", "s");

        Assert.False(reset);
        await AssertNoKeyWasTouchedAsync();
    }

    [Fact]
    public async Task Resetting_With_A_Token_Is_Refused_For_A_Deactivated_Account()
    {
        Scenario s = await ArrangeAsync();
        await SetResetTokenAsync(s.Victim.Id, "T");
        await _db.AdminCredentials.Where(c => c.Id == s.Victim.Id).ExecuteUpdateAsync(c => c.SetProperty(x => x.IsActive, false));
        string hashBefore = (await StoredAsync(s.Victim.Id)).PasswordHash;

        bool reset = await AuthAs(s.Victim).ResetPasswordAsync("T", "Changed123!");

        Assert.False(reset);
        Assert.Equal(hashBefore, (await StoredAsync(s.Victim.Id)).PasswordHash);
        await AssertNoKeyWasTouchedAsync();
    }

    [Fact]
    public async Task A_Refused_Deactivation_Leaves_The_Accounts_Api_Keys_Alone()
    {
        AdminCredential lastOwner = await SeedAccountAsync("only-owner@example.com", UserRoles.Owner);
        await SeedKeyAsync(lastOwner, "live");

        bool deactivated = await _repository.TryDeactivateAsync(lastOwner);

        Assert.False(deactivated);
        Assert.Null((await RevokedAtByNameAsync())["live"]);
    }
}
