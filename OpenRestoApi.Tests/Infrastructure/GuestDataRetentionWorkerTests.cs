using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Infrastructure.Retention;

namespace OpenRestoApi.Tests.Infrastructure;

/// <summary>The scheduling around <see cref="GuestDataRetentionService"/>; the cutoff rule is <c>GuestDataRetentionServiceTests</c>'.</summary>
public class GuestDataRetentionWorkerTests
{
    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow => new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    }

    private static GuestDataRetentionWorker CreateWorker(Mock<IEmailFailureRepository> failures)
    {
        var retention = new GuestDataRetentionService(
            failures.Object, new Mock<IAdminNotificationRepository>().Object,
            new FixedClock(), Options.Create(new RetentionSettings()));

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(GuestDataRetentionService))).Returns(retention);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);

        return new GuestDataRetentionWorker(
            factory.Object, NullLogger<GuestDataRetentionWorker>.Instance, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
    }

    private static async Task RunUntilAsync(GuestDataRetentionWorker worker, Func<bool> done)
    {
        await worker.StartAsync(CancellationToken.None);
        try
        {
            for (int attempt = 0; attempt < 100 && !done(); attempt++)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PrunesRepeatedly_RatherThanOnceAtStartup()
    {
        var failures = new Mock<IEmailFailureRepository>();
        int passes = 0;
        failures.Setup(r => r.DeleteOlderThanAsync(It.IsAny<DateTime>()))
            .ReturnsAsync(1)
            .Callback(() => Interlocked.Increment(ref passes));

        await RunUntilAsync(CreateWorker(failures), () => Volatile.Read(ref passes) >= 2);

        Assert.True(Volatile.Read(ref passes) >= 2, $"Expected repeated passes, saw {passes}.");
    }

    [Fact]
    public async Task SurvivesAFailedPass_AndTriesAgainOnTheNextTick()
    {
        var failures = new Mock<IEmailFailureRepository>();
        int attempts = 0;
        failures.Setup(r => r.DeleteOlderThanAsync(It.IsAny<DateTime>()))
            .Callback(() => Interlocked.Increment(ref attempts))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        await RunUntilAsync(CreateWorker(failures), () => Volatile.Read(ref attempts) >= 2);

        Assert.True(Volatile.Read(ref attempts) >= 2, $"Expected a retry after the failure, saw {attempts}.");
    }
}
