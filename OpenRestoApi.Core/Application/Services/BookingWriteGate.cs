namespace OpenRestoApi.Core.Application.Services;

/// <summary>
/// One lock for every path that decides which table or group a booking sits on. A conflict check
/// followed by an insert is two steps, and nothing in the database stops two requests passing the
/// first before either runs the second, so the pair runs inside <see cref="EnterAsync"/>. Must be a
/// singleton shared by <see cref="BookingService"/>, <see cref="AdminService"/> and
/// <see cref="WaitlistService"/>: a gate per service would serialize each path against itself only.
/// Holding it keeps the check and the write together; never hold it across an email or push.
/// </summary>
/// <remarks>
/// ponytail: process-wide, which is correct for the documented single-instance deployment (as is
/// <c>HoldService</c>). A second instance needs a database-level guard instead.
/// </remarks>
/// <seealso>BookingConcurrencyTests.ConcurrentBookingsOfOneTable_OnlyOneIsCreated</seealso>
/// <seealso>BookingConcurrencyTests.ConcurrentBookingsOfDifferentTablesOrTimes_AreAllCreated</seealso>
/// <seealso>BookingConcurrencyTests.ConcurrentPublicAndAdminBookingsOfOneTable_OnlyOneIsCreated</seealso>
/// <seealso>BookingConcurrencyTests.ConcurrentMovesOntoOneSlot_OnlyOneBookingMoves</seealso>
/// <seealso>BookingConcurrencyTests.ConcurrentWaitlistSeatsOnOneTable_OnlyOnePartyIsSeated</seealso>
/// <seealso>BookingConcurrencyTests.ConcurrentBookingsFillingTheCoverCap_AdmitOnlyWhatFits</seealso>
public sealed class BookingWriteGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync()
    {
        await _semaphore.WaitAsync();
        return new Releaser(_semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
