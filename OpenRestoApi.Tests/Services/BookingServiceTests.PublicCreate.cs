using Moq;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Mappings;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

// The public POST /api/bookings surface: what a guest-supplied body may not decide (server-owned
// fields), what a failed request must not leave behind (holds), and when the location accepts a
// sitting at all (opening hours).
public partial class BookingServiceTests
{
    private const string Guest = "guest@example.com";

    private static DateTime Noon(int daysAhead) => DateTime.UtcNow.Date.AddDays(daysAhead).AddHours(12);

    private static void SeedBooking(AppDbContext db, int id, int tableId, DateTime date, string email = "owner@example.com")
    {
        db.Bookings.Add(new Booking
        {
            Id = id, RestaurantId = 1, TableId = tableId, SectionId = 1, Date = date,
            EndTime = date.AddHours(1), Seats = 2, CustomerEmail = email, BookingRef = $"ref-{id}"
        });
        db.SaveChanges();
    }

    // ── Opening hours ────────────────────────────────────────────────────────

    private static void SeedRestaurantOpen(AppDbContext db, string open, string close, string? openDays = null)
    {
        TestSeed.RestaurantWithHours(db, open, close);
        if (openDays is not null)
        {
            db.Restaurants.Find(1)!.OpenDays = openDays;
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task CreateBookingAsync_AcceptsBooking_InsideOpeningHours()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_AcceptsBooking_InsideOpeningHours));
        SeedRestaurantOpen(db, "11:00", "14:00");

        BookingDto result = await CreateService(db).CreateBookingAsync(
            new BookingDto { RestaurantId = 1, SectionId = 1, TableId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(10) });

        Assert.NotEmpty(result.BookingRef!);
    }

    [Fact]
    public async Task CreateBookingAsync_RejectsBooking_OutsideOpeningHours()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_RejectsBooking_OutsideOpeningHours));
        SeedRestaurantOpen(db, "11:00", "14:00");

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() => CreateService(db).CreateBookingAsync(
            new BookingDto { RestaurantId = 1, SectionId = 1, TableId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(10).AddHours(8) }));

        Assert.Equal(ErrorCodes.RestaurantClosedAtTime, ex.Code);
        Assert.Empty(db.Bookings);
    }

    [Fact]
    public async Task CreateBookingAsync_RejectsBooking_OnAClosedDay()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_RejectsBooking_OnAClosedDay));
        DateTime date = Noon(10);
        int isoDay = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        string otherDays = string.Join(",", Enumerable.Range(1, 7).Where(d => d != isoDay));
        SeedRestaurantOpen(db, "11:00", "14:00", otherDays);

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() => CreateService(db).CreateBookingAsync(
            new BookingDto { RestaurantId = 1, SectionId = 1, TableId = 1, CustomerEmail = Guest, Seats = 2, Date = date }));

        Assert.Equal(ErrorCodes.RestaurantClosedAtTime, ex.Code);
    }

    [Fact]
    public async Task CreateBookingAsync_RejectsGroupBooking_OutsideOpeningHours()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_RejectsGroupBooking_OutsideOpeningHours));
        SeedRestaurantWithGroup(db);
        Restaurant restaurant = db.Restaurants.Find(1)!;
        restaurant.OpenTime = "11:00";
        restaurant.CloseTime = "14:00";
        db.SaveChanges();

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(() => CreateService(db).CreateBookingAsync(
            new BookingDto { RestaurantId = 1, TableGroupId = 1, CustomerEmail = Guest, Seats = 6, Date = Noon(10).AddHours(8) }));

        Assert.Equal(ErrorCodes.RestaurantClosedAtTime, ex.Code);
    }

    // ── Server-owned fields ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateBookingAsync_IgnoresAnIdFromTheRequest_AndLeavesTheOtherBookingAlone()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_IgnoresAnIdFromTheRequest_AndLeavesTheOtherBookingAlone));
        TestSeed.BasicRestaurant(db);
        SeedBooking(db, id: 7, tableId: 1, Noon(5));
        IHoldService holds = NewHoldService();

        BookingDto result = await CreateService(db, holds).CreateBookingAsync(
            new BookingDto { Id = 7, RestaurantId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(6) });

        Assert.NotEqual(7, result.Id);
        Booking original = db.Bookings.Single(b => b.Id == 7);
        Assert.Equal("owner@example.com", original.CustomerEmail);
        Assert.Equal(2, db.Bookings.Count());
        Assert.Equal(0, holds.GetActiveHoldsCount());
    }

    [Fact]
    public async Task CreateBookingAsync_IgnoresTheCancellationFlagsFromTheRequest()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_IgnoresTheCancellationFlagsFromTheRequest));
        TestSeed.BasicRestaurant(db);
        BookingService svc = CreateService(db);

        await svc.CreateBookingAsync(new BookingDto
        {
            RestaurantId = 1, SectionId = 1, TableId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(5),
            IsCancelled = true, CancelledAt = DateTime.UtcNow
        });

        Booking saved = Assert.Single(db.Bookings);
        Assert.False(saved.IsCancelled);
        Assert.Null(saved.CancelledAt);
        // A booking that arrived "cancelled" would stop blocking its table; this one still does.
        await Assert.ThrowsAsync<ConflictException>(() => svc.CreateBookingAsync(
            new BookingDto { RestaurantId = 1, SectionId = 1, TableId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(5) }));
    }

    // ── Holds placed by auto-assign ──────────────────────────────────────────

    [Fact]
    public async Task CreateBookingAsync_AutoAssign_CountsTheAutoHoldTowardTheClientLimit()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_AutoAssign_CountsTheAutoHoldTowardTheClientLimit));
        TestSeed.BasicRestaurant(db);
        IHoldService holds = NewHoldService();
        for (int i = 0; i < OpenRestoApi.Infrastructure.Holds.HoldService.MaxHoldsPerClient; i++)
        {
            Assert.NotNull(holds.PlaceHold(1, 100 + i, 1, Noon(8), clientKey: "203.0.113.9"));
        }

        BookingDto autoAssign() => new() { RestaurantId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(5) };

        ConflictException ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService(db, holds).CreateBookingAsync(autoAssign(), clientKey: "203.0.113.9"));
        Assert.Equal(ErrorCodes.HoldClientLimit, ex.Code);

        BookingDto other = await CreateService(db, holds).CreateBookingAsync(autoAssign(), clientKey: "198.51.100.4");
        Assert.NotEmpty(other.BookingRef!);
    }

    [Fact]
    public async Task CreateBookingAsync_AutoAssign_ReleasesItsHold_WhenTheInsertFails()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_AutoAssign_ReleasesItsHold_WhenTheInsertFails));
        TestSeed.BasicRestaurant(db);
        IHoldService holds = NewHoldService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceWithFailingInsert(db, holds).CreateBookingAsync(
                new BookingDto { RestaurantId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(5) }));

        Assert.Equal(0, holds.GetActiveHoldsCount());
    }

    [Fact]
    public async Task CreateBookingAsync_AutoAssignedGroup_ReleasesItsHold_WhenTheInsertFails()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_AutoAssignedGroup_ReleasesItsHold_WhenTheInsertFails));
        SeedRestaurantWithGroup(db);
        IHoldService holds = NewHoldService();

        // Six guests fit no single table, so auto-assign lands on the 8-seat group.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceWithFailingInsert(db, holds).CreateBookingAsync(
                new BookingDto { RestaurantId = 1, CustomerEmail = Guest, Seats = 6, Date = Noon(5) }));

        Assert.Equal(0, holds.GetActiveHoldsCount());
    }

    [Fact]
    public async Task CreateBookingAsync_KeepsACallersOwnHold_WhenTheInsertFails()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(CreateBookingAsync_KeepsACallersOwnHold_WhenTheInsertFails));
        TestSeed.BasicRestaurant(db);
        IHoldService holds = NewHoldService();
        HoldResult mine = holds.PlaceHold(1, 1, 1, Noon(5))!;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ServiceWithFailingInsert(db, holds).CreateBookingAsync(
                new BookingDto { RestaurantId = 1, CustomerEmail = Guest, Seats = 2, Date = Noon(5), HoldId = mine.HoldId }));

        Assert.NotNull(holds.GetHold(mine.HoldId));
    }

    private static BookingService ServiceWithFailingInsert(AppDbContext db, IHoldService holds)
    {
        TestSeed.OpenAllDay(db);
        var bookings = new Mock<IBookingRepository>();
        bookings.Setup(r => r.AddAsync(It.IsAny<Booking>())).ThrowsAsync(new InvalidOperationException("insert failed"));
        return new BookingService(
            bookings.Object,
            new TableRepository(db),
            new RestaurantRepository(db),
            holds,
            new BookingMapper(),
            new TableAutoAssigner(new BookingRepository(db), holds),
            new TableGroupRepository(db));
    }
}
