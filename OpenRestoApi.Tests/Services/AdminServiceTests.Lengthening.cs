using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

public partial class AdminServiceTests
{
    private static readonly DateTime SittingStart = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>Booking 1 sits on table 1 from 19:00 to 20:00; table 1 is in group 1 with table 2.</summary>
    private async Task<Booking> SeedSittingOnAGroupedTableAsync()
    {
        SeedTwoTablesInOneGroup();
        var sitting = new Booking
        {
            Id = 1, RestaurantId = 1, SectionId = 1, TableId = 1, Seats = 2,
            Date = SittingStart, EndTime = SittingStart.AddMinutes(60), BookingRef = "SITTING",
        };
        _db.Bookings.Add(sitting);
        await _db.SaveChangesAsync();
        return sitting;
    }

    private async Task AddBookingAsync(int id, int? tableId, int? groupId, DateTime start, int minutes = 60, bool cancelled = false)
    {
        _db.Bookings.Add(new Booking
        {
            Id = id, RestaurantId = 1, SectionId = 1, TableId = tableId, TableGroupId = groupId, Seats = 2,
            Date = start, EndTime = start.AddMinutes(minutes), BookingRef = $"B{id}", IsCancelled = cancelled,
        });
        await _db.SaveChangesAsync();
    }

    private async Task<DateTime?> StoredEndAsync(int id)
    {
        _db.ChangeTracker.Clear();
        return (await _db.Bookings.FindAsync(id))!.EndTime;
    }

    // ── Extend ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtendBookingAsync_Extends_WhenNothingFollowsOnTheTable()
    {
        await SeedSittingOnAGroupedTableAsync();

        DateTime? end = await CreateService().ExtendBookingAsync(1, 30);

        Assert.Equal(SittingStart.AddMinutes(90), end);
    }

    [Fact]
    public async Task ExtendBookingAsync_Rejects_WhenAnotherBookingStartsInsideTheAddedTime()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, SittingStart.AddMinutes(75));

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().ExtendBookingAsync(1, 30));

        Assert.Equal(ErrorCodes.BookingExtendConflict, ex.Code);
        Assert.Equal(SittingStart.AddMinutes(60), await StoredEndAsync(1));
    }

    [Fact]
    public async Task ExtendBookingAsync_Extends_UpToTheMomentTheNextBookingStarts()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, SittingStart.AddMinutes(90));

        DateTime? end = await CreateService().ExtendBookingAsync(1, 30);

        Assert.Equal(SittingStart.AddMinutes(90), end);
    }

    [Fact]
    public async Task ExtendBookingAsync_Rejects_OneMinutePastTheMomentTheNextBookingStarts()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, SittingStart.AddMinutes(89));

        await Assert.ThrowsAsync<ConflictException>(() => CreateService().ExtendBookingAsync(1, 30));
    }

    [Fact]
    public async Task ExtendBookingAsync_Ignores_ACancelledBookingInTheAddedTime()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, SittingStart.AddMinutes(75), cancelled: true);

        DateTime? end = await CreateService().ExtendBookingAsync(1, 30);

        Assert.Equal(SittingStart.AddMinutes(90), end);
    }

    [Fact]
    public async Task ExtendBookingAsync_Rejects_WhenTheTablesGroupIsBookedInTheAddedTime()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: null, groupId: 1, SittingStart.AddMinutes(75));

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().ExtendBookingAsync(1, 30));

        Assert.Equal(ErrorCodes.BookingExtendConflict, ex.Code);
    }

    [Fact]
    public async Task ExtendBookingAsync_OfAGroupBooking_Rejects_WhenAMemberIsBookedInTheAddedTime()
    {
        SeedTwoTablesInOneGroup();
        await _db.SaveChangesAsync();
        await AddBookingAsync(1, tableId: null, groupId: 1, SittingStart);
        await AddBookingAsync(2, tableId: 2, groupId: null, SittingStart.AddMinutes(75));

        await Assert.ThrowsAsync<ConflictException>(() => CreateService().ExtendBookingAsync(1, 30));
    }

    [Fact]
    public async Task ExtendBookingAsync_Extends_WhenOnlyASiblingMemberIsBookedInTheAddedTime()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 2, groupId: null, SittingStart.AddMinutes(75));

        DateTime? end = await CreateService().ExtendBookingAsync(1, 30);

        Assert.Equal(SittingStart.AddMinutes(90), end);
    }

    // Shortening hands time back, so it never asks whether the time is free.
    [Fact]
    public async Task ExtendBookingAsync_Shortens_EvenWhenAnotherBookingOverlapsTheSitting()
    {
        await SeedSittingOnAGroupedTableAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, SittingStart.AddMinutes(30));

        DateTime? end = await CreateService().ExtendBookingAsync(1, -15);

        Assert.Equal(SittingStart.AddMinutes(45), end);
    }

    // ── Extend every in-progress sitting ─────────────────────────────────────

    [Fact]
    public async Task ExtendAllActiveBookingsAsync_Skips_ASittingWhoseAddedTimeIsTaken()
    {
        SeedTwoTablesInOneGroup();
        await _db.SaveChangesAsync();
        DateTime start = DateTime.UtcNow.AddMinutes(-30);
        await AddBookingAsync(1, tableId: 1, groupId: null, start);
        await AddBookingAsync(2, tableId: 2, groupId: null, start);
        await AddBookingAsync(3, tableId: 1, groupId: null, start.AddMinutes(75));

        ExtendRestaurantBookingsResponse? result = await CreateService().ExtendAllActiveBookingsAsync(1, 30);

        Assert.Equal(["B2"], result!.ExtendedBookings.Select(b => b.BookingRef));
        SkippedExtensionDto skipped = Assert.Single(result.SkippedBookings);
        Assert.Equal((1, "B1", 1, "T1"), (skipped.Id, skipped.BookingRef, skipped.TableId, skipped.TableName));
        Assert.Equal(start.AddMinutes(60), await StoredEndAsync(1));
        Assert.Equal(start.AddMinutes(90), await StoredEndAsync(2));
    }

    [Fact]
    public async Task ExtendAllActiveBookingsAsync_Extends_UpToTheMomentTheNextBookingStarts()
    {
        SeedTwoTablesInOneGroup();
        await _db.SaveChangesAsync();
        DateTime start = DateTime.UtcNow.AddMinutes(-30);
        await AddBookingAsync(1, tableId: 1, groupId: null, start);
        await AddBookingAsync(2, tableId: 1, groupId: null, start.AddMinutes(90));

        ExtendRestaurantBookingsResponse? result = await CreateService().ExtendAllActiveBookingsAsync(1, 30);

        Assert.Single(result!.ExtendedBookings);
        Assert.Empty(result.SkippedBookings);
        Assert.Equal(start.AddMinutes(90), await StoredEndAsync(1));
    }

    [Fact]
    public async Task ExtendAllActiveBookingsAsync_Skips_AGroupSitting_WhenAMemberTableIsBookedAfterIt()
    {
        SeedTwoTablesInOneGroup();
        await _db.SaveChangesAsync();
        DateTime start = DateTime.UtcNow.AddMinutes(-30);
        await AddBookingAsync(1, tableId: null, groupId: 1, start);
        await AddBookingAsync(2, tableId: 2, groupId: null, start.AddMinutes(75));

        ExtendRestaurantBookingsResponse? result = await CreateService().ExtendAllActiveBookingsAsync(1, 30);

        Assert.Empty(result!.ExtendedBookings);
        SkippedExtensionDto skipped = Assert.Single(result.SkippedBookings);
        Assert.Equal((1, (int?)null, (int?)1), (skipped.Id, skipped.TableId, skipped.TableGroupId));
        Assert.Equal(start.AddMinutes(60), await StoredEndAsync(1));
    }

    [Fact]
    public void SkippedExtensionDto_CarriesNoGuestDetails()
    {
        Assert.DoesNotContain(typeof(SkippedExtensionDto).GetProperties(), p => p.Name.StartsWith("Customer"));
    }

    // ── Undo of a finish or no-show ──────────────────────────────────────────

    private async Task<DateTime> SeedFinishedSittingAsync()
    {
        Booking booking = await SeedSittingAsync(startedMinutesAgo: 30, lengthMinutes: 90, status: BookingStatus.Seated);
        await CreateService().SetBookingStatusAsync(1, "Finished");
        return booking.Date.AddMinutes(90);
    }

    [Fact]
    public async Task SetBookingStatusAsync_Undo_Rejects_WhenAnotherBookingTookTheFreedTime()
    {
        await SeedFinishedSittingAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, DateTime.UtcNow.AddMinutes(10));

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().SetBookingStatusAsync(1, "Seated"));

        Assert.Equal(ErrorCodes.BookingUndoConflict, ex.Code);
        _db.ChangeTracker.Clear();
        Booking stored = (await _db.Bookings.FindAsync(1))!;
        Assert.Equal(BookingStatus.Finished, stored.Status);
        Assert.True(stored.EndTime <= DateTime.UtcNow);
    }

    [Fact]
    public async Task SetBookingStatusAsync_Undo_Restores_UpToTheMomentTheNextBookingStarts()
    {
        DateTime originalEnd = await SeedFinishedSittingAsync();
        await AddBookingAsync(2, tableId: 1, groupId: null, originalEnd);

        BookingDetailDto? undone = await CreateService().SetBookingStatusAsync(1, "Seated");

        Assert.Equal("Seated", undone!.Status);
        Assert.Equal(originalEnd, undone.EndTime!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetBookingStatusAsync_Undo_Rejects_WhenTheTablesGroupTookTheFreedTime()
    {
        await SeedFinishedSittingAsync();
        _db.TableGroups.Add(new TableGroup { Id = 1, RestaurantId = 1, CombinedSeats = 8 });
        _db.Tables.Add(new Table { Id = 2, Name = "T2", Seats = 4, SectionId = 1 });
        await _db.SaveChangesAsync();
        _db.TableGroupMemberships.Add(new TableGroupMembership { TableGroupId = 1, TableId = 1 });
        _db.TableGroupMemberships.Add(new TableGroupMembership { TableGroupId = 1, TableId = 2 });
        await _db.SaveChangesAsync();
        await AddBookingAsync(2, tableId: null, groupId: 1, DateTime.UtcNow.AddMinutes(10));

        await Assert.ThrowsAsync<ConflictException>(() => CreateService().SetBookingStatusAsync(1, "Seated"));
    }

    // Going forward only ever shortens the sitting, so it needs no free time.
    [Fact]
    public async Task SetBookingStatusAsync_Finishing_NeverAsksWhetherTheTimeIsFree()
    {
        await SeedSittingAsync(startedMinutesAgo: 30, lengthMinutes: 90, status: BookingStatus.Seated);
        await AddBookingAsync(2, tableId: 1, groupId: null, DateTime.UtcNow.AddMinutes(10));

        BookingDetailDto? finished = await CreateService().SetBookingStatusAsync(1, "Finished");

        Assert.Equal("Finished", finished!.Status);
    }
}
