using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>Real SQLite: the repositories prune with <c>ExecuteDeleteAsync</c>, which the in-memory provider cannot run.</summary>
public class GuestDataRetentionServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow => Now;
    }

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public GuestDataRetentionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Restaurants.Add(new Restaurant { Id = 1, Name = "R" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestDataRetentionService CreateService(int failureDays = RetentionSettings.DefaultEmailFailureDays, int notificationDays = RetentionSettings.DefaultNotificationDays)
        => new(new EmailFailureRepository(_db), new AdminNotificationRepository(_db), new FixedClock(),
            Options.Create(new RetentionSettings { EmailFailureDays = failureDays, NotificationDays = notificationDays }));

    private void AddFailure(string recipient, DateTime attemptedAt)
        => _db.EmailFailures.Add(new EmailFailure { RecipientEmail = recipient, ErrorMessage = "x", AttemptedAt = attemptedAt });

    private void AddNotification(string name, DateTime createdAt)
        => _db.AdminNotifications.Add(new AdminNotification { RestaurantId = 1, CustomerName = name, Type = "BookingCreated", CreatedAt = createdAt });

    [Fact]
    public async Task PruneAsync_DeletesRowsOlderThanTheirWindow()
    {
        AddFailure("old@example.com", Now.AddDays(-RetentionSettings.DefaultEmailFailureDays).AddMinutes(-1));
        AddNotification("old", Now.AddDays(-RetentionSettings.DefaultNotificationDays).AddMinutes(-1));
        await _db.SaveChangesAsync();

        int removed = await CreateService().PruneAsync();

        Assert.Equal(2, removed);
        Assert.Empty(_db.EmailFailures);
        Assert.Empty(_db.AdminNotifications);
    }

    [Fact]
    public async Task PruneAsync_KeepsRowsInsideTheirWindow()
    {
        AddFailure("young@example.com", Now.AddDays(-RetentionSettings.DefaultEmailFailureDays).AddMinutes(1));
        AddNotification("young", Now.AddDays(-RetentionSettings.DefaultNotificationDays).AddMinutes(1));
        await _db.SaveChangesAsync();

        int removed = await CreateService().PruneAsync();

        Assert.Equal(0, removed);
        Assert.Single(_db.EmailFailures);
        Assert.Single(_db.AdminNotifications);
    }

    [Fact]
    public async Task PruneAsync_DeletesNothing_WhenAWindowIsDisabled()
    {
        AddFailure("old@example.com", Now.AddDays(-400));
        AddNotification("old", Now.AddDays(-400));
        await _db.SaveChangesAsync();

        Assert.Equal(0, await CreateService(failureDays: 0, notificationDays: -1).PruneAsync());

        Assert.Single(_db.EmailFailures);
        Assert.Single(_db.AdminNotifications);
    }
}
