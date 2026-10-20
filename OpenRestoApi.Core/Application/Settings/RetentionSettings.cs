namespace OpenRestoApi.Core.Application.Settings;

/// <summary>
/// Bound from the <c>Retention</c> configuration section. The email-failure list and the
/// notification feed copy a guest's name, address and booking reference, and unlike a booking
/// nothing ever removes them unless an admin purges that exact booking. Zero or negative disables
/// the pass for that table.
/// </summary>
public class RetentionSettings
{
    public const int DefaultEmailFailureDays = 30;
    public const int DefaultNotificationDays = 90;

    public int EmailFailureDays { get; set; } = DefaultEmailFailureDays;
    public int NotificationDays { get; set; } = DefaultNotificationDays;
}
