using OpenRestoApi.Core.Application.Exceptions;

namespace OpenRestoApi.Core.Application.Utilities;

/// <summary>
/// Guards the guest-authored booking text that staff read in a terminal (<c>openresto bookings
/// list</c>) and in emails. An ESC, CSI/OSC introducer or C1 control in a name can clear the screen
/// or retitle the window of whoever prints it, so those characters are refused at the door instead
/// of being left for every reader to scrub. Rejected rather than stripped: stripping would store a
/// name the guest did not type, and a legitimate guest never sends one of these.
/// </summary>
/// <seealso>GuestTextTests.CustomerName_RejectsEveryControlCharacter</seealso>
/// <seealso>GuestTextTests.SpecialRequests_AllowsLineBreaksAndTabsOnly</seealso>
public static class GuestText
{
    public static void RejectControlCharacters(string? customerName, string? specialRequests)
    {
        if (HasControlCharacter(customerName, allowLineBreaks: false))
        {
            throw new ValidationException("The name can't contain control characters. Please retype it.") { Code = ErrorCodes.BookingNameInvalid };
        }

        if (HasControlCharacter(specialRequests, allowLineBreaks: true))
        {
            throw new ValidationException("The special requests can't contain control characters. Please retype them.") { Code = ErrorCodes.BookingSpecialRequestsInvalid };
        }
    }

    /// <summary>C0 (U+0000-U+001F), DEL and C1 (U+0080-U+009F); free text may keep <c>\n</c>, <c>\r</c> and <c>\t</c>.</summary>
    private static bool HasControlCharacter(string? value, bool allowLineBreaks)
        => value is not null && value.Any(c => char.IsControl(c) && !(allowLineBreaks && c is '\n' or '\r' or '\t'));
}
