using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.TestInfrastructure;

/// <summary>
/// Creates a fresh EF in-memory DbContext with a unique name per test to avoid
/// cross-test state leakage. The standard pattern across service tests.
/// </summary>
internal static class TestDbFactory
{
    public static AppDbContext Create(string name)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(opts);
    }

    /// <summary>
    /// A real SQLite database, named per test like <see cref="Create"/>, that lives as long as the
    /// returned context. For code that leans on SQL the in-memory provider cannot run
    /// (<c>ExecuteUpdate</c>, transactions).
    /// </summary>
    public static AppDbContext CreateSqlite(string name)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{name}?mode=memory&cache=shared")
            .Options;
        var db = new AppDbContext(opts);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }
}
