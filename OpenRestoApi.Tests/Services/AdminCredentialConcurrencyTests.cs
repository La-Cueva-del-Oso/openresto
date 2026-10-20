using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
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
            .UseSqlite($"Data Source={_path}")
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
    {
        var start = new TaskCompletionSource();
        Task<Exception?>[] running = [.. operations.Select(op => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                await op();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }))];
        start.SetResult();
        return await Task.WhenAll(running);
    }

    private async Task<int> ActiveOwnersAsync()
    {
        using AppDbContext db = NewContext();
        return await db.AdminCredentials.CountAsync(c => c.IsActive && c.Role == UserRoles.Owner);
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
}
