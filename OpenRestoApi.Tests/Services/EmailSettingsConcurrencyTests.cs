using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// SMTP saves run as separate requests would: one context each, against a SQLite file. The race
/// is forced rather than hoped for: the rival save runs from inside the first one's password
/// encryption, after it has read and validated the row and before it writes.
/// </summary>
public sealed class EmailSettingsConcurrencyTests : IDisposable
{
    private const string RealHost = "smtp.real.example";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"openresto-email-concurrency-{Guid.NewGuid():N}.db");
    private readonly List<AppDbContext> _contexts = [];

    public EmailSettingsConcurrencyTests()
    {
        using AppDbContext db = NewContext();
        db.Database.EnsureCreated();
        db.EmailSettings.Add(new EmailSettings { Host = RealHost, Port = 587, Username = "mailer", EncryptedPassword = "enc:P0" });
        db.SaveChanges();
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

    private EmailSettingsService Service(Action? whileEncrypting = null)
    {
        AppDbContext db = NewContext();
        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Encrypt(It.IsAny<string>())).Returns<string>(plain =>
        {
            whileEncrypting?.Invoke();
            return "enc:" + plain;
        });
        return new EmailSettingsService(
            new EmailSettingsRepository(db),
            new Mock<IEmailFailureRepository>().Object,
            protector.Object,
            new Mock<IEmailService>().Object);
    }

    private async Task<EmailSettings> StoredAsync()
    {
        using AppDbContext db = NewContext();
        return await db.EmailSettings.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task A_Password_Saved_For_The_Real_Server_Never_Ends_Up_Beside_A_Concurrently_Saved_Server()
    {
        EmailSettingsService manager = Service();
        EmailSettingsService owner = Service(whileEncrypting: () => Task.Run(() =>
            manager.SaveAsync("smtp.attacker.example", 587, "mailer", "fake", true, null, null)).GetAwaiter().GetResult());

        await owner.SaveAsync(RealHost, 587, "mailer", "P2", true, null, null);

        EmailSettings stored = await StoredAsync();
        Assert.Equal((RealHost, "enc:P2"), (stored.Host, stored.EncryptedPassword));
    }

    [Fact]
    public async Task A_Save_With_The_Destination_Unchanged_And_No_Password_Keeps_The_Stored_One()
    {
        await Service().SaveAsync(RealHost, 587, "mailer", null, true, "Front desk", "desk@example.com");

        EmailSettings stored = await StoredAsync();
        Assert.Equal("enc:P0", stored.EncryptedPassword);
        Assert.Equal(("Front desk", "desk@example.com"), (stored.FromName, stored.FromEmail));
    }
}
