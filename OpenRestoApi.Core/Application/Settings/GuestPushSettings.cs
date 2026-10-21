namespace OpenRestoApi.Core.Application.Settings;

/// <summary>
/// Guest booking reminders. Bound from the <c>GuestPush</c> configuration section
/// (<c>GuestPush__ReminderLeadHours</c>, <c>GuestPush__ExpoAccessToken</c> as environment variables).
/// </summary>
public class GuestPushSettings
{
    public const string DefaultReminderLeadHours = "24,2";

    /// <summary>
    /// Optional. Expo's push service accepts unauthenticated sends by default; an EAS project
    /// that enabled "enhanced push security" requires the token minted for it.
    /// </summary>
    public string? ExpoAccessToken { get; set; }

    /// <summary>
    /// Comma-separated hours before the sitting at which a reminder goes out, e.g. "24,2".
    /// Parsed by <see cref="ReminderLeads"/>; anything unparseable falls back to the default.
    /// </summary>
    public string ReminderLeadHours { get; set; } = DefaultReminderLeadHours;

    /// <summary>
    /// Devices that may hold reminders for one booking. Opt-in needs only a reference and an email,
    /// so without a cap one booking could be given an unbounded number of addresses to push to.
    /// Re-registering a device already on the booking is never refused.
    /// </summary>
    public int MaxDevicesPerBooking { get; set; } = 5;

    /// <summary>
    /// Most pushes one reminder pass sends. What is still due when the pass reaches it is sent by
    /// the next pass (the worker runs every minute); it is not marked as sent.
    /// </summary>
    public int MaxSendsPerPass { get; set; } = 200;

    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    private TimeSpan _sendTimeout = DefaultSendTimeout;

    /// <summary>
    /// How long one push may take before it counts as that subscription's failure. Zero,
    /// negative (including "infinite") and anything <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// cannot take become <see cref="DefaultSendTimeout"/>: left as set, they would fail every push
    /// at once or throw out of the reminder pass.
    /// </summary>
    /// <seealso>GuestPushSettingsTests.SendTimeout_ReplacesAValueTheTimerCannotTakeWithTheDefault</seealso>
    /// <seealso>GuestPushSettingsTests.SendTimeout_KeepsAPositiveValue</seealso>
    public TimeSpan SendTimeout
    {
        get => _sendTimeout;
        set => _sendTimeout = value > TimeSpan.Zero && value.TotalMilliseconds < uint.MaxValue ? value : DefaultSendTimeout;
    }

    /// <summary>
    ///
    /// </summary>
    /// <seealso>GuestPushSettingsTests.ReminderLeads_ParsesDistinctPositiveHoursDescending</seealso>
    /// <seealso>GuestPushSettingsTests.ReminderLeads_FallsBackToTheDefaultWhenNothingParses</seealso>
    public IReadOnlyList<int> ReminderLeads()
    {
        List<int> parsed = Parse(ReminderLeadHours);
        return parsed.Count > 0 ? parsed : Parse(DefaultReminderLeadHours);
    }

    private static List<int> Parse(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out int hours) ? hours : 0)
            .Where(hours => hours > 0)
            .Distinct()
            .OrderByDescending(hours => hours)
            .ToList();
}
