using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// The account rules that read the table and then write it, run as separate requests would:
/// one context (and so one connection) each, against a SQLite file. Every scenario repeats
/// <see cref="Rounds"/> times because a lost race is a matter of timing, not of the first run.
/// </summary>
public sealed class AdminCredentialConcurrencyTests : IDisposable
{
    private const int Rounds = 25;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"openresto-concurrency-{Guid.NewGuid():N}.db");
    private readonly List<AppDbContext> _contexts = [];

    public AdminCredentialConcurrencyTests()
    {
        using AppDbContext db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        foreach (AppDbContext db in _contexts)
        {
            db.Dispose();
        }

        SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private AppDbContext NewContext()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_path}", o => o.ExecutionStrategy(d => new SqliteRetryingExecutionStrategy(d)))
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options);
        db.Database.OpenConnection();
        _contexts.Add(db);
        return db;
    }

    private UserService UserServiceAs(AdminCredential? caller = null)
    {
        AppDbContext db = NewContext();
        return new UserService(
            new AdminCredentialRepository(db),
            new PasswordService(),
            caller is null ? FakeCurrentUser.Anonymous() : FakeCurrentUser.For(caller),
            new AdminPushSubscriptionRepository(db));
    }

    private async Task<List<AdminCredential>> SeedOwnersAsync(int count)
    {
        using AppDbContext db = NewContext();
        await db.AdminCredentials.ExecuteDeleteAsync();
        List<AdminCredential> owners = [.. Enumerable.Range(0, count).Select(i => new AdminCredential
        {
            Email = $"owner{i}@example.com",
            PasswordHash = "h",
            PasswordSalt = "s",
            Role = UserRoles.Owner,
        })];
        db.AdminCredentials.AddRange(owners);
        await db.SaveChangesAsync();
        return owners;
    }

    private static async Task<Exception?[]> RunTogetherAsync(params Func<Task>[] operations)
        => await RunTogetherForResultsAsync([.. operations.Select(op => (Func<Task<Exception?>>)(async () =>
        {
            try
            {
                await op();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }))]);

    private static async Task<T[]> RunTogetherForResultsAsync<T>(Func<Task<T>>[] operations)
    {
        var start = new TaskCompletionSource();
        Task<T>[] running = [.. operations.Select(op => Task.Run(async () =>
        {
            await start.Task;
            return await op();
        }))];
        start.SetResult();
        return await Task.WhenAll(running);
    }

    private async Task<int> ActiveOwnersAsync()
    {
        using AppDbContext db = NewContext();
        return await db.AdminCredentials.CountAsync(c => c.IsActive && c.Role == UserRoles.Owner);
    }

    private async Task<AdminCredential> SeedAccountWithAnswerAsync(string answer, string? password = null)
    {
        using AppDbContext db = NewContext();
        await db.AdminCredentials.ExecuteDeleteAsync();
        (string hash, string salt) = new PasswordService().Hash(answer);
        (string passwordHash, string passwordSalt) = password is null ? ("h", "s") : new PasswordService().Hash(password);
        var account = new AdminCredential
        {
            Email = "owner@example.com",
            PasswordHash = passwordHash,
            PasswordSalt = passwordSalt,
            PvqQuestion = "Q?",
            PvqAnswerHash = hash,
            PvqAnswerSalt = salt,
        };
        db.AdminCredentials.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private SecurityQuestionsService SecurityQuestions(IPasswordService? passwords = null, AdminCredential? caller = null)
    {
        AppDbContext db = NewContext();
        return new SecurityQuestionsService(
            new AdminCredentialRepository(db),
            passwords ?? new PasswordService(),
            caller is null ? FakeCurrentUser.Anonymous() : FakeCurrentUser.For(caller));
    }

    private AuthService AuthServiceAs(
        AdminCredential? caller = null, IPasswordService? passwords = null, IAuditScope? audit = null, IAdminCredentialRepository? repository = null)
    {
        AppDbContext db = NewContext();
        var jwt = new Mock<IJwtTokenService>();
        jwt.Setup(j => j.Generate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>())).Returns("token");
        return new AuthService(
            repository ?? new AdminCredentialRepository(db),
            passwords ?? new PasswordService(),
            jwt.Object,
            caller is null ? FakeCurrentUser.Anonymous() : FakeCurrentUser.For(caller),
            new AdminPushSubscriptionRepository(db),
            audit);
    }

    private async Task SeedPushSubscriptionAsync(int userId)
    {
        using AppDbContext db = NewContext();
        db.AdminPushSubscriptions.Add(new AdminPushSubscription { UserId = userId, Endpoint = $"https://push.example/{userId}", P256dh = "p", Auth = "a" });
        await db.SaveChangesAsync();
    }

    private async Task<int> PushSubscriptionCountAsync(int userId)
    {
        using AppDbContext db = NewContext();
        return await db.AdminPushSubscriptions.CountAsync(s => s.UserId == userId);
    }

    private static void AssertNothingAudited(Mock<IAuditScope> audit)
    {
        audit.Verify(a => a.RecordChange(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<object?>()), Times.Never);
        audit.Verify(a => a.Describe(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>()), Times.Never);
    }

    private async Task<AdminCredential> SeedAccountWithResetTokenAsync(string token)
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");
        using AppDbContext db = NewContext();
        await db.AdminCredentials.Where(c => c.Id == account.Id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.ResetToken, token)
            .SetProperty(c => c.ResetTokenExpiry, DateTime.UtcNow.AddMinutes(15)));
        return await ReloadAsync(account.Id);
    }

    private async Task<bool> PasswordIsAsync(int id, string password)
    {
        AdminCredential stored = await ReloadAsync(id);
        return new PasswordService().Verify(password, stored.PasswordHash, stored.PasswordSalt);
    }

    private Task ResetByOwnerAsync(int id)
        => UserServiceAs().ResetPasswordAsync(id, new ResetUserPasswordRequest { NewPassword = "OwnerReset123!" });

    /// <summary>
    /// Hashes and verifies like <see cref="PasswordService"/>, but the first call runs
    /// <paramref name="interruption"/> to completion before returning. Every flow under test hashes
    /// or verifies after reading the account and before writing it, so the interruption lands in
    /// exactly the gap a concurrent request would.
    /// </summary>
    private sealed class PasswordServiceInterruptedOnce(Func<Task> interruption) : IPasswordService
    {
        private readonly PasswordService _inner = new();
        private Func<Task>? _interruption = interruption;

        public (string Hash, string Salt) Hash(string password)
        {
            (string Hash, string Salt) result = _inner.Hash(password);
            Interrupt();
            return result;
        }

        public bool Verify(string password, string storedHash, string storedSalt)
        {
            bool result = _inner.Verify(password, storedHash, storedSalt);
            Interrupt();
            return result;
        }

        private void Interrupt()
        {
            Func<Task>? pending = Interlocked.Exchange(ref _interruption, null);
            if (pending != null)
            {
                Task.Run(pending).GetAwaiter().GetResult();
            }
        }
    }

    private async Task<AdminCredential> ReloadAsync(int id)
    {
        using AppDbContext db = NewContext();
        return await db.AdminCredentials.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static void AssertRefusedAs(string code, Exception? refusal)
        => Assert.Equal(code, Assert.IsType<BusinessRuleException>(refusal).Code);

    // ── Last active Owner ───────────────────────────────────────────────────────

    [Fact]
    public async Task Two_Owners_Deactivating_Each_Other_Concurrently_Leave_One_Active_Owner()
    {
        for (int round = 0; round < Rounds; round++)
        {
            List<AdminCredential> owners = await SeedOwnersAsync(2);
            UserService byFirst = UserServiceAs(owners[0]);
            UserService bySecond = UserServiceAs(owners[1]);

            Exception?[] outcomes = await RunTogetherAsync(
                () => byFirst.SetActiveAsync(owners[1].Id, new SetUserActiveRequest { IsActive = false }),
                () => bySecond.SetActiveAsync(owners[0].Id, new SetUserActiveRequest { IsActive = false }));

            Assert.Single(outcomes, o => o is null);
            AssertRefusedAs(ErrorCodes.UserLastActiveOwnerDeactivate, outcomes.Single(o => o is not null));
            Assert.Equal(1, await ActiveOwnersAsync());
        }
    }

    [Fact]
    public async Task Two_Owners_Demoting_Each_Other_Concurrently_Leave_One_Active_Owner()
    {
        for (int round = 0; round < Rounds; round++)
        {
            List<AdminCredential> owners = await SeedOwnersAsync(2);
            UserService byFirst = UserServiceAs(owners[0]);
            UserService bySecond = UserServiceAs(owners[1]);

            Exception?[] outcomes = await RunTogetherAsync(
                () => byFirst.UpdateRoleAsync(owners[1].Id, new UpdateUserRoleRequest { Role = UserRoles.Manager }),
                () => bySecond.UpdateRoleAsync(owners[0].Id, new UpdateUserRoleRequest { Role = UserRoles.Manager }));

            Assert.Single(outcomes, o => o is null);
            AssertRefusedAs(ErrorCodes.UserLastActiveOwnerDemote, outcomes.Single(o => o is not null));
            Assert.Equal(1, await ActiveOwnersAsync());
        }
    }

    [Fact]
    public async Task Deactivations_Succeed_Concurrently_While_Another_Active_Owner_Remains()
    {
        for (int round = 0; round < Rounds; round++)
        {
            List<AdminCredential> owners = await SeedOwnersAsync(3);
            UserService first = UserServiceAs();
            UserService second = UserServiceAs();

            Exception?[] outcomes = await RunTogetherAsync(
                () => first.SetActiveAsync(owners[0].Id, new SetUserActiveRequest { IsActive = false }),
                () => second.SetActiveAsync(owners[1].Id, new SetUserActiveRequest { IsActive = false }));

            Assert.All(outcomes, Assert.Null);
            Assert.Equal(1, await ActiveOwnersAsync());
        }
    }

    [Fact]
    public async Task Three_Owners_Deactivated_Concurrently_Keep_Exactly_One()
    {
        for (int round = 0; round < Rounds; round++)
        {
            List<AdminCredential> owners = await SeedOwnersAsync(3);
            Exception?[] outcomes = await RunTogetherAsync(
                [.. owners.Select(o =>
                {
                    UserService svc = UserServiceAs();
                    return (Func<Task>)(() => svc.SetActiveAsync(o.Id, new SetUserActiveRequest { IsActive = false }));
                })]);

            Assert.Equal(2, outcomes.Count(o => o is null));
            AssertRefusedAs(ErrorCodes.UserLastActiveOwnerDeactivate, outcomes.Single(o => o is not null));
            Assert.Equal(1, await ActiveOwnersAsync());
        }
    }

    [Fact]
    public async Task Demotions_Succeed_Concurrently_While_Another_Active_Owner_Remains()
    {
        for (int round = 0; round < Rounds; round++)
        {
            List<AdminCredential> owners = await SeedOwnersAsync(3);
            UserService first = UserServiceAs();
            UserService second = UserServiceAs();

            Exception?[] outcomes = await RunTogetherAsync(
                () => first.UpdateRoleAsync(owners[0].Id, new UpdateUserRoleRequest { Role = UserRoles.Manager }),
                () => second.UpdateRoleAsync(owners[1].Id, new UpdateUserRoleRequest { Role = UserRoles.Manager }));

            Assert.All(outcomes, Assert.Null);
            Assert.Equal(1, await ActiveOwnersAsync());
        }
    }

    // ── Session revocation ──────────────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_Revocations_Are_All_Counted()
    {
        const int Revocations = 4;
        for (int round = 0; round < Rounds; round++)
        {
            AdminCredential account = (await SeedOwnersAsync(1)).Single();
            var staleReaders = new List<(AdminCredentialRepository Repository, AdminCredential Account)>();
            for (int i = 0; i < Revocations; i++)
            {
                var repository = new AdminCredentialRepository(NewContext());
                staleReaders.Add((repository, (await repository.GetByIdAsync(account.Id))!));
            }

            await RunTogetherAsync([.. staleReaders.Select(r => (Func<Task>)(() => r.Repository.RevokeSessionsAsync(r.Account)))]);

            Assert.Equal(Revocations, (await ReloadAsync(account.Id)).SessionVersion);
        }
    }

    // ── Security-question lockout ───────────────────────────────────────────────

    [Fact]
    public async Task Concurrent_Wrong_Answers_Lock_The_Account_After_The_Limit()
    {
        const int Max = SecurityQuestionsService.MaxFailedAttempts;
        for (int round = 0; round < Rounds; round++)
        {
            AdminCredential account = await SeedAccountWithAnswerAsync("right");
            PvqVerifyOutcome[] outcomes = await RunTogetherForResultsAsync(
                [.. Enumerable.Range(0, Max * 2).Select(_ =>
                {
                    SecurityQuestionsService svc = SecurityQuestions();
                    return (Func<Task<PvqVerifyOutcome>>)(() => svc.VerifyAsync(account.Email, "wrong"));
                })]);

            Assert.Equal(Max, outcomes.Count(o => o.Status == PvqVerifyStatus.WrongAnswer));
            Assert.Equal(Max, outcomes.Count(o => o.Status == PvqVerifyStatus.LockedOut));
            AdminCredential stored = await ReloadAsync(account.Id);
            Assert.True(stored.PvqLockedUntil > DateTime.UtcNow);
            Assert.Equal(0, stored.PvqFailedAttempts);
            Assert.Equal(PvqVerifyStatus.LockedOut, (await SecurityQuestions().VerifyAsync(account.Email, "right")).Status);
        }
    }

    [Fact]
    public async Task Concurrent_Wrong_Answers_Below_The_Limit_Are_All_Counted()
    {
        const int Max = SecurityQuestionsService.MaxFailedAttempts;
        for (int round = 0; round < Rounds; round++)
        {
            AdminCredential account = await SeedAccountWithAnswerAsync("right");
            PvqVerifyOutcome[] outcomes = await RunTogetherForResultsAsync(
                [.. Enumerable.Range(0, Max - 1).Select(_ =>
                {
                    SecurityQuestionsService svc = SecurityQuestions();
                    return (Func<Task<PvqVerifyOutcome>>)(() => svc.VerifyAsync(account.Email, "wrong"));
                })]);

            Assert.All(outcomes, o => Assert.Equal(PvqVerifyStatus.WrongAnswer, o.Status));
            AdminCredential counted = await ReloadAsync(account.Id);
            Assert.Equal(Max - 1, counted.PvqFailedAttempts);
            Assert.Null(counted.PvqLockedUntil);

            Assert.Equal(PvqVerifyStatus.Success, (await SecurityQuestions().VerifyAsync(account.Email, "right")).Status);
            Assert.Equal(0, (await ReloadAsync(account.Id)).PvqFailedAttempts);
        }
    }

    // ── Recovery writes held to the account as it was read ──────────────────────

    [Fact]
    public async Task A_Security_Answer_Checked_Before_An_Owner_Reset_Earns_No_Reset_Token()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("old-answer");
        SecurityQuestionsService verifier = SecurityQuestions(new PasswordServiceInterruptedOnce(() => ResetByOwnerAsync(account.Id)));

        PvqVerifyOutcome outcome = await verifier.VerifyAsync(account.Email, "old-answer");

        Assert.Equal(PvqVerifyStatus.WrongAnswer, outcome.Status);
        Assert.Null((await ReloadAsync(account.Id)).ResetToken);
    }

    [Fact]
    public async Task A_Security_Answer_Checked_Before_The_Question_Changed_Earns_No_Reset_Token()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("old-answer");
        (string newHash, string newSalt) = new PasswordService().Hash("new-answer");
        SecurityQuestionsService verifier = SecurityQuestions(new PasswordServiceInterruptedOnce(async () =>
        {
            using AppDbContext db = NewContext();
            await db.AdminCredentials.Where(c => c.Id == account.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PvqAnswerHash, newHash)
                .SetProperty(c => c.PvqAnswerSalt, newSalt));
        }));

        PvqVerifyOutcome outcome = await verifier.VerifyAsync(account.Email, "old-answer");

        Assert.Equal(PvqVerifyStatus.WrongAnswer, outcome.Status);
        Assert.Null((await ReloadAsync(account.Id)).ResetToken);
    }

    [Fact]
    public async Task A_Security_Question_Set_Up_While_An_Owner_Resets_The_Password_Is_Not_Stored()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("old-answer", password: "Current123!");
        SecurityQuestionsService setup = SecurityQuestions(
            new PasswordServiceInterruptedOnce(() => ResetByOwnerAsync(account.Id)), caller: account);

        bool stored = await setup.SetupAsync("Current123!", "Planted?", "planted-answer");

        Assert.False(stored);
        AdminCredential after = await ReloadAsync(account.Id);
        Assert.Null(after.PvqQuestion);
        Assert.Null(after.PvqAnswerHash);
    }

    [Fact]
    public async Task A_Reset_Link_Read_Before_The_Password_Changed_Cannot_Set_A_Password_Afterwards()
    {
        AdminCredential account = await SeedAccountWithResetTokenAsync("T");
        AuthService attacker = AuthServiceAs(passwords: new PasswordServiceInterruptedOnce(
            () => AuthServiceAs(caller: account).ChangePasswordAsync("Current123!", "UserChose123!")));

        bool reset = await attacker.ResetPasswordAsync("T", "Attacker123!");

        Assert.False(reset);
        Assert.True(await PasswordIsAsync(account.Id, "UserChose123!"));
    }

    [Fact]
    public async Task A_Reset_Link_Is_Spent_Once_When_Two_Requests_Read_It()
    {
        AdminCredential account = await SeedAccountWithResetTokenAsync("T");
        bool secondReset = false;
        AuthService first = AuthServiceAs(passwords: new PasswordServiceInterruptedOnce(
            async () => secondReset = await AuthServiceAs().ResetPasswordAsync("T", "Second123!")));

        bool firstReset = await first.ResetPasswordAsync("T", "First123!");

        Assert.Equal((false, true), (firstReset, secondReset));
        Assert.True(await PasswordIsAsync(account.Id, "Second123!"));
        Assert.Null((await ReloadAsync(account.Id)).ResetToken);
    }

    // ── Password and email changes held to the account as it was read ───────────

    [Fact]
    public async Task A_Password_Change_Read_Before_An_Owner_Reset_Is_Refused()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");
        AuthService change = AuthServiceAs(account, new PasswordServiceInterruptedOnce(() => ResetByOwnerAsync(account.Id)));

        string? token = await change.ChangePasswordAsync("Current123!", "UserChose123!");

        Assert.Null(token);
        Assert.True(await PasswordIsAsync(account.Id, "OwnerReset123!"));
    }

    [Fact]
    public async Task A_Password_Change_Applies_When_Nothing_Intervened()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");

        string? token = await AuthServiceAs(account).ChangePasswordAsync("Current123!", "UserChose123!");

        Assert.Equal("token", token);
        Assert.True(await PasswordIsAsync(account.Id, "UserChose123!"));
    }

    [Fact]
    public async Task A_Refused_Password_Change_Keeps_Push_Subscriptions_And_Writes_No_Audit_Entry()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");
        await SeedPushSubscriptionAsync(account.Id);
        var audit = new Mock<IAuditScope>();
        AuthService change = AuthServiceAs(
            account,
            new PasswordServiceInterruptedOnce(() => AuthServiceAs(account).LogoutAsync()),
            audit.Object);

        string? token = await change.ChangePasswordAsync("Current123!", "UserChose123!");

        Assert.Null(token);
        Assert.True(await PasswordIsAsync(account.Id, "Current123!"));
        Assert.Equal(1, await PushSubscriptionCountAsync(account.Id));
        AssertNothingAudited(audit);
    }

    [Fact]
    public async Task An_Email_Change_Applies_When_Nothing_Intervened()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");

        string? token = await AuthServiceAs(account).ChangeEmailAsync("Current123!", "New@Example.com");

        Assert.Equal("token", token);
        Assert.Equal("new@example.com", (await ReloadAsync(account.Id)).Email);
    }

    [Fact]
    public async Task An_Email_Change_Read_Before_An_Owner_Reset_Is_Refused()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");
        var audit = new Mock<IAuditScope>();
        AuthService change = AuthServiceAs(
            account, new PasswordServiceInterruptedOnce(() => ResetByOwnerAsync(account.Id)), audit.Object);

        string? token = await change.ChangeEmailAsync("Current123!", "new@example.com");

        Assert.Null(token);
        Assert.Equal("owner@example.com", (await ReloadAsync(account.Id)).Email);
        AssertNothingAudited(audit);
    }

    [Fact]
    public async Task An_Email_Change_Leaves_The_Session_Version_Alone()
    {
        AdminCredential account = await SeedAccountWithAnswerAsync("answer", password: "Current123!");

        await AuthServiceAs(account).ChangeEmailAsync("Current123!", "new@example.com");

        Assert.Equal(account.SessionVersion, (await ReloadAsync(account.Id)).SessionVersion);
    }

    [Fact]
    public async Task Two_Accounts_Changing_To_The_Same_Email_Concurrently_Leave_One_Owner_Of_It()
    {
        AdminCredential first = await SeedAccountWithAnswerAsync("answer", password: "Current123!");
        AdminCredential second;
        using (AppDbContext seed = NewContext())
        {
            (string hash, string salt) = new PasswordService().Hash("Current123!");
            second = new AdminCredential { Email = "second@example.com", PasswordHash = hash, PasswordSalt = salt };
            seed.AdminCredentials.Add(second);
            await seed.SaveChangesAsync();
        }

        // The lookup that says the address is free has answered when the other account takes it.
        var real = new AdminCredentialRepository(NewContext());
        int interruptions = 0;
        var repository = new Mock<IAdminCredentialRepository>();
        repository.Setup(r => r.GetByIdAsync(It.IsAny<int>())).Returns((int id) => real.GetByIdAsync(id));
        repository.Setup(r => r.TryChangeEmailAsync(It.IsAny<AdminCredential>(), It.IsAny<string>()))
            .Returns((AdminCredential c, string e) => real.TryChangeEmailAsync(c, e));
        repository.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).Returns(async (string email) =>
        {
            AdminCredential? found = await real.GetByEmailAsync(email);
            if (Interlocked.Increment(ref interruptions) == 1)
            {
                Assert.NotNull(await AuthServiceAs(second).ChangeEmailAsync("Current123!", "taken@example.com"));
            }

            return found;
        });

        Exception? refusal = await Record.ExceptionAsync(
            () => AuthServiceAs(first, repository: repository.Object).ChangeEmailAsync("Current123!", "taken@example.com"));

        AssertRefusedAs(ErrorCodes.AuthEmailAlreadyInUse, refusal);
        Assert.Equal("owner@example.com", (await ReloadAsync(first.Id)).Email);
        Assert.Equal("taken@example.com", (await ReloadAsync(second.Id)).Email);
    }
}
