using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

public partial class AdminServiceTests
{
    private static readonly DateTime GroupSlot = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>Tables 1 and 2 in one group (id 1), which is not itself booked yet.</summary>
    private void SeedTwoTablesInOneGroup()
    {
        _db.Restaurants.Add(new Restaurant { Id = 1, Name = "Test", Timezone = "UTC", DefaultBookingDurationMinutes = 60 });
        _db.Sections.Add(new Section { Id = 1, Name = "Main", RestaurantId = 1 });
        _db.Tables.Add(new Table { Id = 1, Name = "T1", Seats = 4, SectionId = 1 });
        _db.Tables.Add(new Table { Id = 2, Name = "T2", Seats = 4, SectionId = 1 });
        _db.TableGroups.Add(new TableGroup { Id = 1, RestaurantId = 1, CombinedSeats = 8 });
        _db.TableGroupMemberships.Add(new TableGroupMembership { TableGroupId = 1, TableId = 1 });
        _db.TableGroupMemberships.Add(new TableGroupMembership { TableGroupId = 1, TableId = 2 });
    }

    private static AdminCreateBookingRequest WalkInAt(int tableId, DateTime date) => new()
    {
        RestaurantId = 1, SectionId = 1, TableId = tableId, Date = date, Seats = 2, CustomerEmail = "door@example.com",
    };

    [Fact]
    public async Task CreateBookingAsync_Creates_OnAFreeTableOfAGroup()
    {
        SeedTwoTablesInOneGroup();
        await _db.SaveChangesAsync();

        BookingDetailDto result = await CreateService().CreateBookingAsync(WalkInAt(1, GroupSlot));

        Assert.Equal(1, result.TableId);
    }

    // A group booking stores TableId = null, so a table-only check reads the table as free while
    // the group has it seated.
    [Fact]
    public async Task CreateBookingAsync_Rejects_ATableWhoseGroupIsBookedThen()
    {
        SeedTwoTablesInOneGroup();
        _db.Bookings.Add(new Booking
        {
            RestaurantId = 1, SectionId = 1, TableGroupId = 1, Seats = 6,
            Date = GroupSlot, EndTime = GroupSlot.AddMinutes(60), BookingRef = "GROUP",
        });
        await _db.SaveChangesAsync();

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().CreateBookingAsync(WalkInAt(1, GroupSlot)));

        Assert.Equal(ErrorCodes.BookingTableConflict, ex.Code);
        Assert.Single(_db.Bookings);
    }

    [Fact]
    public async Task CreateBookingAsync_Creates_AfterTheGroupsSittingEnds()
    {
        SeedTwoTablesInOneGroup();
        _db.Bookings.Add(new Booking
        {
            RestaurantId = 1, SectionId = 1, TableGroupId = 1, Seats = 6,
            Date = GroupSlot, EndTime = GroupSlot.AddMinutes(60), BookingRef = "GROUP",
        });
        await _db.SaveChangesAsync();

        BookingDetailDto result = await CreateService().CreateBookingAsync(WalkInAt(1, GroupSlot.AddMinutes(60)));

        Assert.Equal(1, result.TableId);
    }

    // Grouping only deprioritizes a member: its sibling stays individually bookable.
    [Fact]
    public async Task CreateBookingAsync_Creates_OnASiblingMember_WhenOnlyTheOtherMemberIsBooked()
    {
        SeedTwoTablesInOneGroup();
        _db.Bookings.Add(new Booking
        {
            RestaurantId = 1, SectionId = 1, TableId = 2, Seats = 2,
            Date = GroupSlot, EndTime = GroupSlot.AddMinutes(60), BookingRef = "T2",
        });
        await _db.SaveChangesAsync();

        BookingDetailDto result = await CreateService().CreateBookingAsync(WalkInAt(1, GroupSlot));

        Assert.Equal(1, result.TableId);
    }
}
