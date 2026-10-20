namespace OpenRestoApi.Core.Application.DTOs;

/// <summary>
/// Filter parameters for the admin bookings grid query. Mirrors the arguments of
/// <c>AdminService.GetBookingsAsync</c>; consumers build this record and hand it to
/// <see cref="Interfaces.IBookingFilterRepository.QueryAsync"/> so the service stays
/// free of IQueryable / .Where / .Include chains.
/// </summary>
public sealed class BookingFilter
{
    public int? RestaurantId { get; init; }
    public DateTime? BookingDate { get; init; }
    /// <summary>Raw status string from the API ("active"|"past"|"cancelled"|"noshow"|"all"|"upcoming").</summary>
    public string Status { get; init; } = "active";
    public string? Email { get; init; }
    public string? BookingRef { get; init; }

    /// <summary>
    /// Free-text admin search, matched as a case-insensitive substring against the customer
    /// name, the customer email and the booking reference at once. Combines with
    /// <see cref="Email"/>/<see cref="BookingRef"/> rather than replacing them: those stay the
    /// narrow single-field filters the deep links use.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// Whether <see cref="Query"/> may match the customer name and email. False for a caller
    /// <c>BookingGuestVisibility</c> redacts, so the search cannot be used to read back the
    /// identity the response hides.
    /// </summary>
    /// <seealso>AdminServiceTests.GetBookingsAsync_QueryDoesNotMatchGuestIdentity_ForAnApiKeyWithoutGuestsRead</seealso>
    /// <seealso>AdminServiceTests.GetBookingsAsync_QueryMatchesGuestIdentity_ForAnApiKeyWithGuestsRead</seealso>
    public bool QueryMatchesGuestIdentity { get; init; } = true;
}
