using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

public class AuthServiceTests
{
    // 32+ char HS256 test key.
    private const string TestKey = "test-key-must-be-at-least-32-characters-long-for-hs256!!";

    private static IConfiguration BuildConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = TestKey,
            ["Jwt:Issuer"] = "openresto-tests",
            ["Jwt:Audience"] = "openresto-tests",
        }).Build();

    private static AuthService CreateService(AppDbContext db, ICurrentUserService? currentUser = null)
    {
        return new AuthService(
            new AdminCredentialRepository(db),
            new PasswordService(),
            new JwtTokenService(BuildConfig()),
            currentUser ?? FakeCurrentUser.Anonymous());
    }

    private static void SeedPushSubscription(AppDbContext db, int userId, string endpoint)
    {
        db.AdminPushSubscriptions.Add(new AdminPushSubscription
        {
            UserId = userId,
            Endpoint = endpoint,
            P256dh = "p",
            Auth = "a",
            CreatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static AdminCredential SeedCredential(
        AppDbContext db,
        string email,
        string password,
        string role = UserRoles.Owner,
        bool isActive = true,
        string? displayName = null)
    {
        var passwords = new PasswordService();
        (string hash, string salt) = passwords.Hash(password);
        var cred = new AdminCredential
        {
            Email = email,
            PasswordHash = hash,
            PasswordSalt = salt,
            Role = role,
            IsActive = isActive,
            DisplayName = displayName,
        };
        db.AdminCredentials.Add(cred);
        db.SaveChanges();
        return cred;
    }

    // ── LoginAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_Returns_Jwt_On_Correct_Email_And_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Returns_Jwt_On_Correct_Email_And_Password));
        SeedCredential(db, "admin@example.com", "secret123");
        AuthService svc = CreateService(db);

        string? jwt = await svc.LoginAsync("admin@example.com", "secret123");

        Assert.False(string.IsNullOrWhiteSpace(jwt));
    }

    [Fact]
    public async Task LoginAsync_Is_Case_Insensitive_On_Email()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Is_Case_Insensitive_On_Email));
        SeedCredential(db, "admin@example.com", "secret123");
        AuthService svc = CreateService(db);

        string? jwt = await svc.LoginAsync("ADMIN@EXAMPLE.COM", "secret123");

        Assert.False(string.IsNullOrWhiteSpace(jwt));
    }

    [Fact]
    public async Task LoginAsync_Authenticates_The_Account_Matching_The_Email()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Authenticates_The_Account_Matching_The_Email));
        SeedCredential(db, "owner@example.com", "owner-pw");
        SeedCredential(db, "manager@example.com", "manager-pw", role: UserRoles.Manager);
        AuthService svc = CreateService(db);

        // The second account's own password works, and the first account's does not —
        // i.e. login resolves a row rather than comparing against "the" credential.
        Assert.NotNull(await svc.LoginAsync("manager@example.com", "manager-pw"));
        Assert.Null(await svc.LoginAsync("manager@example.com", "owner-pw"));
    }

    [Fact]
    public async Task LoginAsync_Puts_The_Users_Id_And_Role_On_The_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Puts_The_Users_Id_And_Role_On_The_Token));
        AdminCredential cred = SeedCredential(db, "manager@example.com", "secret123", role: UserRoles.Manager);
        AuthService svc = CreateService(db);

        string? jwt = await svc.LoginAsync("manager@example.com", "secret123");

        var decoded = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt);
        Assert.Equal(
            cred.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decoded.Claims.Single(c => c.Type == System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(
            UserRoles.Manager,
            decoded.Claims.Single(c => c.Type == System.Security.Claims.ClaimTypes.Role).Value);
    }

    [Fact]
    public async Task LoginAsync_Returns_Null_On_Wrong_Email()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Returns_Null_On_Wrong_Email));
        SeedCredential(db, "admin@example.com", "secret123");
        AuthService svc = CreateService(db);

        string? jwt = await svc.LoginAsync("wrong@example.com", "secret123");

        Assert.Null(jwt);
    }

    [Fact]
    public async Task LoginAsync_Returns_Null_On_Wrong_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Returns_Null_On_Wrong_Password));
        SeedCredential(db, "admin@example.com", "secret123");
        AuthService svc = CreateService(db);

        string? jwt = await svc.LoginAsync("admin@example.com", "wrong");

        Assert.Null(jwt);
    }

    [Fact]
    public async Task LoginAsync_Returns_Null_For_A_Deactivated_Account()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Returns_Null_For_A_Deactivated_Account));
        SeedCredential(db, "disabled@example.com", "secret123", isActive: false);
        AuthService svc = CreateService(db);

        Assert.Null(await svc.LoginAsync("disabled@example.com", "secret123"));
    }

    [Fact]
    public async Task LoginAsync_Does_Not_Invent_An_Account_When_None_Exists()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LoginAsync_Does_Not_Invent_An_Account_When_None_Exists));
        AuthService svc = CreateService(db);

        // The bootstrap-on-login behaviour is gone: creating the first account is the
        // startup seeder's job, so an unknown email is simply a failed login.
        Assert.Null(await svc.LoginAsync("admin@example.com", "secret123"));
        Assert.Empty(db.AdminCredentials);
    }

    // ── GetCurrentUserAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentUserAsync_Returns_The_Callers_Identity()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(GetCurrentUserAsync_Returns_The_Callers_Identity));
        SeedCredential(db, "owner@example.com", "pw");
        AdminCredential manager = SeedCredential(
            db, "manager@example.com", "pw", role: UserRoles.Manager, displayName: "Sam");
        AuthService svc = CreateService(db, FakeCurrentUser.For(manager));

        CurrentUserDto? me = await svc.GetCurrentUserAsync();

        Assert.NotNull(me);
        Assert.Equal(manager.Id, me!.Id);
        Assert.Equal("manager@example.com", me.Email);
        Assert.Equal("Sam", me.DisplayName);
        Assert.Equal(UserRoles.Manager, me.Role);
    }

    [Fact]
    public async Task GetCurrentUserAsync_Returns_Null_When_The_Token_Names_No_Live_Account()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(GetCurrentUserAsync_Returns_Null_When_The_Token_Names_No_Live_Account));
        AdminCredential cred = SeedCredential(db, "disabled@example.com", "pw", isActive: false);
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        Assert.Null(await svc.GetCurrentUserAsync());
    }

    // ── ChangePasswordAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePasswordAsync_Rejects_Wrong_Current_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Rejects_Wrong_Current_Password));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        string? jwt = await svc.ChangePasswordAsync("wrong", "newpass");

        Assert.Null(jwt);
        Assert.Equal(0, cred.SessionVersion);
        // Old password still works after failed change attempt.
        Assert.NotNull(await svc.LoginAsync("admin@example.com", "old"));
    }

    [Fact]
    public async Task ChangePasswordAsync_Succeeds_And_New_Password_Works()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Succeeds_And_New_Password_Works));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        string? jwt = await svc.ChangePasswordAsync("old", "newpass");

        Assert.NotNull(jwt);
        Assert.Null(await svc.LoginAsync("admin@example.com", "old"));
        Assert.NotNull(await svc.LoginAsync("admin@example.com", "newpass"));
    }

    [Fact]
    public async Task ChangePasswordAsync_Changes_Only_The_Callers_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Changes_Only_The_Callers_Password));
        SeedCredential(db, "owner@example.com", "owner-pw");
        AdminCredential manager = SeedCredential(db, "manager@example.com", "manager-pw", role: UserRoles.Manager);
        AuthService svc = CreateService(db, FakeCurrentUser.For(manager));

        Assert.NotNull(await svc.ChangePasswordAsync("manager-pw", "manager-new"));

        Assert.NotNull(await svc.LoginAsync("manager@example.com", "manager-new"));
        Assert.NotNull(await svc.LoginAsync("owner@example.com", "owner-pw"));
    }

    [Fact]
    public async Task ChangePasswordAsync_Resolves_Caller_By_Email_For_Tokens_Without_A_User_Id()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Resolves_Caller_By_Email_For_Tokens_Without_A_User_Id));
        SeedCredential(db, "legacy@example.com", "old");
        AuthService svc = CreateService(db, FakeCurrentUser.ForLegacyToken("legacy@example.com"));

        Assert.NotNull(await svc.ChangePasswordAsync("old", "newpass"));
    }

    [Fact]
    public async Task ChangePasswordAsync_Returns_Null_When_The_Token_Names_No_Live_Account()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Returns_Null_When_The_Token_Names_No_Live_Account));
        SeedCredential(db, "admin@example.com", "old");
        AuthService svc = CreateService(db, FakeCurrentUser.ForLegacyToken("ghost@example.com"));

        Assert.Null(await svc.ChangePasswordAsync("old", "newpass"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("12345")]
    public async Task ChangePasswordAsync_Throws_ValidationException_When_New_Password_Too_Short(string weak)
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Throws_ValidationException_When_New_Password_Too_Short) + weak);
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.ChangePasswordAsync("pw", weak));
        Assert.Contains("at least 6 characters", ex.Message);
    }

    [Fact]
    public async Task ChangePasswordAsync_Revokes_Sessions_And_Mints_A_Token_At_The_New_Version()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Revokes_Sessions_And_Mints_A_Token_At_The_New_Version));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        string? jwt = await svc.ChangePasswordAsync("old", "newpass");

        Assert.Equal(1, cred.SessionVersion);
        Claim version = Assert.Single(new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims,
            c => c.Type == JwtTokenService.SessionVersionClaim);
        Assert.Equal("1", version.Value);
    }

    [Fact]
    public async Task ChangePasswordAsync_Deletes_The_Callers_Push_Subscriptions_Only()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Deletes_The_Callers_Push_Subscriptions_Only));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        AdminCredential other = SeedCredential(db, "other@example.com", "pw");
        SeedPushSubscription(db, cred.Id, "https://ep/mine");
        SeedPushSubscription(db, other.Id, "https://ep/theirs");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        await svc.ChangePasswordAsync("old", "newpass");

        AdminPushSubscription kept = await db.AdminPushSubscriptions.SingleAsync();
        Assert.Equal(other.Id, kept.UserId);
    }

    [Fact]
    public async Task ChangePasswordAsync_Keeps_Push_Subscriptions_When_The_Current_Password_Is_Wrong()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Keeps_Push_Subscriptions_When_The_Current_Password_Is_Wrong));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        SeedPushSubscription(db, cred.Id, "https://ep/mine");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        Assert.Null(await svc.ChangePasswordAsync("wrong", "newpass"));

        Assert.Equal(1, await db.AdminPushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task ChangePasswordAsync_Invalidates_A_Pending_Reset_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Invalidates_A_Pending_Reset_Token));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        cred.ResetToken = "pending-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        Assert.NotNull(await svc.ChangePasswordAsync("old", "newpass"));

        Assert.False(await svc.ResetPasswordAsync("pending-token", "attacker-pw"));
        Assert.NotNull(await svc.LoginAsync("admin@example.com", "newpass"));
    }

    [Fact]
    public async Task ChangePasswordAsync_Keeps_A_Pending_Reset_Token_When_The_Current_Password_Is_Wrong()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Keeps_A_Pending_Reset_Token_When_The_Current_Password_Is_Wrong));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        cred.ResetToken = "pending-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        Assert.Null(await svc.ChangePasswordAsync("wrong", "newpass"));

        Assert.Equal("pending-token", (await db.AdminCredentials.SingleAsync()).ResetToken);
    }

    [Fact]
    public async Task ChangePasswordAsync_Keeps_The_Security_Question()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangePasswordAsync_Keeps_The_Security_Question));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "old");
        cred.PvqQuestion = "First pet?";
        cred.PvqAnswerHash = "hash";
        cred.PvqAnswerSalt = "salt";
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        Assert.NotNull(await svc.ChangePasswordAsync("old", "newpass"));

        AdminCredential after = await db.AdminCredentials.SingleAsync();
        Assert.Equal("First pet?", after.PvqQuestion);
        Assert.Equal("hash", after.PvqAnswerHash);
        Assert.Equal("salt", after.PvqAnswerSalt);
    }

    // ── ChangeEmailAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeEmailAsync_Returns_New_Jwt_On_Success()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangeEmailAsync_Returns_New_Jwt_On_Success));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        string? jwt = await svc.ChangeEmailAsync("pw", "new@example.com");

        Assert.False(string.IsNullOrWhiteSpace(jwt));
        AdminCredential after = await db.AdminCredentials.SingleAsync();
        Assert.Equal("new@example.com", after.Email);
    }

    [Fact]
    public async Task ChangeEmailAsync_Throws_When_New_Email_Equals_Current()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangeEmailAsync_Throws_When_New_Email_Equals_Current));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        // Same email after normalisation (trim + lower) ⇒ BusinessRuleException.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => svc.ChangeEmailAsync("pw", "ADMIN@EXAMPLE.COM"));
    }

    [Fact]
    public async Task ChangeEmailAsync_Throws_When_Another_Account_Already_Uses_The_Email()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangeEmailAsync_Throws_When_Another_Account_Already_Uses_The_Email));
        SeedCredential(db, "taken@example.com", "pw");
        AdminCredential mover = SeedCredential(db, "mover@example.com", "pw", role: UserRoles.Manager);
        AuthService svc = CreateService(db, FakeCurrentUser.For(mover));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => svc.ChangeEmailAsync("pw", "TAKEN@example.com"));
        Assert.Contains("already in use", ex.Message);
    }

    [Fact]
    public async Task ChangeEmailAsync_Returns_Null_On_Wrong_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangeEmailAsync_Returns_Null_On_Wrong_Password));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        string? jwt = await svc.ChangeEmailAsync("wrong", "new@example.com");

        Assert.Null(jwt);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local.com")]
    [InlineData("   ")]
    public async Task ChangeEmailAsync_Throws_ValidationException_When_Email_Invalid(string malformed)
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ChangeEmailAsync_Throws_ValidationException_When_Email_Invalid) + malformed.GetHashCode());
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.ChangeEmailAsync("pw", malformed));
        Assert.Contains("valid email", ex.Message);
    }

    // ── LogoutAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutAsync_Revokes_The_Callers_Sessions_Only()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LogoutAsync_Revokes_The_Callers_Sessions_Only));
        AdminCredential owner = SeedCredential(db, "owner@example.com", "pw");
        AdminCredential manager = SeedCredential(db, "manager@example.com", "pw", role: UserRoles.Manager);
        AuthService svc = CreateService(db, FakeCurrentUser.For(manager));

        await svc.LogoutAsync();

        Assert.Equal(1, manager.SessionVersion);
        Assert.Equal(0, owner.SessionVersion);
    }

    [Fact]
    public async Task LogoutAsync_Keeps_The_Callers_Push_Subscriptions()
    {
        // Signing out ends the session, not the notifications: the browser keeps delivering
        // bookings, and the next sign-in on it takes the subscription over if it is someone else.
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LogoutAsync_Keeps_The_Callers_Push_Subscriptions));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        SeedPushSubscription(db, cred.Id, "https://ep/mine");
        AuthService svc = CreateService(db, FakeCurrentUser.For(cred));

        await svc.LogoutAsync();

        Assert.Equal(1, cred.SessionVersion);
        Assert.Equal(1, await db.AdminPushSubscriptions.CountAsync());
    }

    [Fact]
    public async Task LogoutAsync_Leaves_Sessions_Alone_For_An_Api_Key()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(LogoutAsync_Leaves_Sessions_Alone_For_An_Api_Key));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db, new FakeCurrentUser { IsApiKeyAuthenticated = true, UserId = cred.Id });

        await svc.LogoutAsync();

        Assert.Equal(0, cred.SessionVersion);
    }

    // ── ResetPasswordAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPasswordAsync_Returns_False_For_Unknown_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Returns_False_For_Unknown_Token));
        SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db);

        bool ok = await svc.ResetPasswordAsync("nonexistent", "freshpw");

        Assert.False(ok);
    }

    [Fact]
    public async Task ResetPasswordAsync_Returns_False_For_Expired_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Returns_False_For_Expired_Token));
        SeedCredential(db, "admin@example.com", "pw");
        // Manually attach an expired reset token to the credential row.
        AdminCredential cred = await db.AdminCredentials.SingleAsync();
        cred.ResetToken = "expired-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db);

        bool ok = await svc.ResetPasswordAsync("expired-token", "freshpw");

        Assert.False(ok);
    }

    [Fact]
    public async Task ResetPasswordAsync_Succeeds_Clears_Token_And_Allows_New_Password()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Succeeds_Clears_Token_And_Allows_New_Password));
        SeedCredential(db, "admin@example.com", "pw");
        AdminCredential cred = await db.AdminCredentials.SingleAsync();
        cred.ResetToken = "valid-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db);

        bool ok = await svc.ResetPasswordAsync("valid-token", "freshpw");

        Assert.True(ok);
        AdminCredential after = await db.AdminCredentials.SingleAsync();
        Assert.Null(after.ResetToken);
        Assert.Null(after.ResetTokenExpiry);
        Assert.Equal(1, after.SessionVersion);
        Assert.Null(await svc.LoginAsync("admin@example.com", "pw"));
        Assert.NotNull(await svc.LoginAsync("admin@example.com", "freshpw"));
    }

    [Fact]
    public async Task ResetPasswordAsync_Deletes_The_Resetting_Accounts_Push_Subscriptions_Only()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Deletes_The_Resetting_Accounts_Push_Subscriptions_Only));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AdminCredential other = SeedCredential(db, "other@example.com", "pw");
        cred.ResetToken = "valid-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        SeedPushSubscription(db, cred.Id, "https://ep/mine");
        SeedPushSubscription(db, other.Id, "https://ep/theirs");
        AuthService svc = CreateService(db);

        Assert.True(await svc.ResetPasswordAsync("valid-token", "freshpw"));

        AdminPushSubscription kept = await db.AdminPushSubscriptions.SingleAsync();
        Assert.Equal(other.Id, kept.UserId);
    }

    [Fact]
    public async Task ResetPasswordAsync_Keeps_Push_Subscriptions_For_An_Unknown_Token()
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Keeps_Push_Subscriptions_For_An_Unknown_Token));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        SeedPushSubscription(db, cred.Id, "https://ep/mine");
        AuthService svc = CreateService(db);

        Assert.False(await svc.ResetPasswordAsync("nonexistent", "freshpw"));

        Assert.Equal(1, await db.AdminPushSubscriptions.CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResetPasswordAsync_Rejects_A_Missing_Token_Without_Touching_Any_Account(string? token)
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Rejects_A_Missing_Token_Without_Touching_Any_Account) + (token ?? "null"));
        AdminCredential cred = SeedCredential(db, "admin@example.com", "pw");
        AuthService svc = CreateService(db);

        bool missing = await svc.ResetPasswordAsync(token!, "freshpw");
        bool invalid = await svc.ResetPasswordAsync("nonexistent", "freshpw");

        Assert.False(missing);
        Assert.Equal(invalid, missing);
        AdminCredential after = await db.AdminCredentials.SingleAsync();
        Assert.Equal(cred.PasswordHash, after.PasswordHash);
        Assert.Equal(0, after.SessionVersion);
        Assert.NotNull(await svc.LoginAsync("admin@example.com", "pw"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("12345")]
    public async Task ResetPasswordAsync_Throws_ValidationException_When_New_Password_Too_Short(string weak)
    {
        using AppDbContext db = TestDbFactory.CreateSqlite(nameof(ResetPasswordAsync_Throws_ValidationException_When_New_Password_Too_Short) + weak);
        SeedCredential(db, "admin@example.com", "pw");
        AdminCredential cred = await db.AdminCredentials.SingleAsync();
        cred.ResetToken = "valid-token";
        cred.ResetTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        await db.SaveChangesAsync();
        AuthService svc = CreateService(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => svc.ResetPasswordAsync("valid-token", weak));
        Assert.Contains("at least 6 characters", ex.Message);
    }
}
