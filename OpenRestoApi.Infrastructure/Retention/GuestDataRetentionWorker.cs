using CustomAccessibility.Attributes;
using OpenRestoApi.Core.Application.Services;

namespace OpenRestoApi.Infrastructure.Retention;

/// <summary>
/// Runs <see cref="GuestDataRetentionService"/> once a day, in <c>AuditRetentionWorker</c>'s
/// shape: a scope per pass, a catch-all so one bad pass does not stop the next, injectable
/// intervals so a test can drive the loop in milliseconds.
/// </summary>
[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Infrastructure.GuestDataRetentionWorkerTests")]
[ExternalAccessAllowed]
internal sealed class GuestDataRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<GuestDataRetentionWorker> logger,
    TimeSpan? startupDelay = null,
    TimeSpan? interval = null) : BackgroundService
{
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromDays(1);

    private readonly TimeSpan _startupDelay = startupDelay ?? DefaultStartupDelay;
    private readonly TimeSpan _interval = interval ?? DefaultInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            do
            {
                await PruneAsync();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure.
        }
    }

    private async Task PruneAsync()
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            int removed = await scope.ServiceProvider.GetRequiredService<GuestDataRetentionService>().PruneAsync();
            if (removed > 0)
            {
                logger.LogInformation("[GuestDataRetention] Removed {Count} expired rows.", removed);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[GuestDataRetention] Prune pass failed.");
        }
    }
}
