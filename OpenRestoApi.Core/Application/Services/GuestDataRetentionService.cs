using Microsoft.Extensions.Options;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Settings;

namespace OpenRestoApi.Core.Application.Services;

/// <summary>
/// Drops email-failure and notification rows past their retention window. Same split as
/// <see cref="AuditRetentionService"/>: the cutoff rule is testable without a running host.
/// <seealso>GuestDataRetentionServiceTests.PruneAsync_DeletesRowsOlderThanTheirWindow</seealso>
/// <seealso>GuestDataRetentionServiceTests.PruneAsync_KeepsRowsInsideTheirWindow</seealso>
/// <seealso>GuestDataRetentionServiceTests.PruneAsync_DeletesNothing_WhenAWindowIsDisabled</seealso>
/// </summary>
public class GuestDataRetentionService(
    IEmailFailureRepository emailFailures,
    IAdminNotificationRepository notifications,
    ISystemClock clock,
    IOptions<RetentionSettings> settings)
{
    private readonly RetentionSettings _settings = settings.Value;

    /// <summary>Returns how many rows were removed across both tables.</summary>
    public async Task<int> PruneAsync()
    {
        int removed = 0;
        if (_settings.EmailFailureDays > 0)
        {
            removed += await emailFailures.DeleteOlderThanAsync(clock.UtcNow.AddDays(-_settings.EmailFailureDays));
        }

        if (_settings.NotificationDays > 0)
        {
            removed += await notifications.DeleteOlderThanAsync(clock.UtcNow.AddDays(-_settings.NotificationDays));
        }

        return removed;
    }
}
