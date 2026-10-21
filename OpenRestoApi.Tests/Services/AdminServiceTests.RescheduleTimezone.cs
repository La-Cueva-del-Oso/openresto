using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

public partial class AdminServiceTests
{
    // New York is UTC-4 on this date, so 19:00 on the wall clock is 23:00 UTC.
    private static readonly DateTime EveningUtc = new(2026, 10, 10, 23, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EveningLocal = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Unspecified);

    private void SeedNewYorkBooking(out Booking moving)
    {
        _db.Restaurants.Add(new Restaurant { Id = 1, Name = "Test", Timezone = "America/New_York", DefaultBookingDurationMinutes = 60 });
        _db.Sections.Add(new Section { Id = 1, Name = "Main", RestaurantId = 1 });
        _db.Tables.Add(new Table { Id = 1, Name = "T1", Seats = 4, SectionId = 1 });
        DateTime start = EveningUtc.AddDays(-3);
        moving = new Booking
        {
            Id = 1, RestaurantId = 1, SectionId = 1, TableId = 1, Seats = 2,
            Date = start, EndTime = start.AddMinutes(60), BookingRef = "MOVING",
        };
        _db.Bookings.Add(moving);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_ReadsAnUnspecifiedDate_AsTheRestaurantsWallClock()
    {
        SeedNewYorkBooking(out _);
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await CreateService().AdminUpdateBookingAsync(
            1, new AdminUpdateBookingRequest { Date = EveningLocal });

        Assert.Equal(EveningUtc, result!.Date);
        Assert.Equal(EveningUtc.AddMinutes(60), result.EndTime);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_KeepsAnExplicitUtcDate_AsThatInstant()
    {
        SeedNewYorkBooking(out _);
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await CreateService().AdminUpdateBookingAsync(
            1, new AdminUpdateBookingRequest { Date = EveningUtc });

        Assert.Equal(EveningUtc, result!.Date);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_RejectsAnUnspecifiedMove_OntoTheSittingAtThatLocalTime()
    {
        SeedNewYorkBooking(out _);
        _db.Bookings.Add(new Booking
        {
            Id = 2, RestaurantId = 1, SectionId = 1, TableId = 1, Seats = 2,
            Date = EveningUtc, EndTime = EveningUtc.AddMinutes(60), BookingRef = "EXISTING",
        });
        await _db.SaveChangesAsync();

        BusinessRuleException ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => CreateService().AdminUpdateBookingAsync(1, new AdminUpdateBookingRequest { Date = EveningLocal }));

        Assert.Equal(ErrorCodes.BookingMoveConflict, ex.Code);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_AllowsAnUnspecifiedMove_ThatOnlyOverlapsTheSameClockTimeInUtc()
    {
        // 19:00 UTC is 15:00 in New York; the other sitting is at 19:00 New York, so they do not meet.
        SeedNewYorkBooking(out _);
        DateTime sameClockInUtc = new(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc);
        _db.Bookings.Add(new Booking
        {
            Id = 2, RestaurantId = 1, SectionId = 1, TableId = 1, Seats = 2,
            Date = sameClockInUtc, EndTime = sameClockInUtc.AddMinutes(60), BookingRef = "OTHER",
        });
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await CreateService().AdminUpdateBookingAsync(
            1, new AdminUpdateBookingRequest { Date = EveningLocal });

        Assert.Equal(EveningUtc, result!.Date);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_DoesNotMoveTheBooking_WhenTheLocalDateIsTheCurrentInstant()
    {
        SeedNewYorkBooking(out Booking moving);
        moving.Date = EveningUtc;
        moving.EndTime = EveningUtc.AddMinutes(90);
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await CreateService().AdminUpdateBookingAsync(
            1, new AdminUpdateBookingRequest { Date = EveningLocal, CustomerName = "Renamed" });

        Assert.Equal(EveningUtc, result!.Date);
        Assert.Equal(EveningUtc.AddMinutes(90), result.EndTime);
        Assert.Equal("Renamed", result.CustomerName);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_ReadsTheDate_InTheTimezoneOfTheRestaurantItMovesTo()
    {
        SeedNewYorkBooking(out _);
        _db.Restaurants.Add(new Restaurant { Id = 2, Name = "West", Timezone = "America/Los_Angeles", DefaultBookingDurationMinutes = 60 });
        _db.Sections.Add(new Section { Id = 2, Name = "Main", RestaurantId = 2 });
        _db.Tables.Add(new Table { Id = 2, Name = "W1", Seats = 4, SectionId = 2 });
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await CreateService().AdminUpdateBookingAsync(
            1, new AdminUpdateBookingRequest { RestaurantId = 2, TableId = 2, Date = EveningLocal });

        // 19:00 in Los Angeles (UTC-7) is 02:00 UTC the next day.
        Assert.Equal(new DateTime(2026, 10, 11, 2, 0, 0, DateTimeKind.Utc), result!.Date);
    }
}
