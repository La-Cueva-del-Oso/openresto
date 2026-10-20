using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// Staff-entered bookings reach the same terminals as guest ones, so the control-character rule
/// of <see cref="GuestText"/> holds on the admin create and update paths too.
/// </summary>
/// <seealso>GuestTextTests.CustomerName_RejectsEveryControlCharacter</seealso>
public partial class AdminServiceTests
{
    [Fact]
    public async Task CreateBookingAsync_RejectsControlCharactersInTheName()
    {
        AdminService svc = CreateService();
        var req = new AdminCreateBookingRequest { CustomerName = "Ada\u001b[2J", RestaurantId = 1, SectionId = 1, TableId = 1 };

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(() => svc.CreateBookingAsync(req));

        Assert.Equal(ErrorCodes.BookingNameInvalid, ex.Code);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_RejectsControlCharactersInNameAndRequests_AndLeavesTheBookingAlone()
    {
        AdminService svc = CreateService();
        SeedBase(1);
        _db.Bookings.Add(new Booking { Id = 1, RestaurantId = 1, SectionId = 1, TableId = 1, Date = DateTime.UtcNow, BookingRef = "B1", CustomerName = "Jane" });
        await _db.SaveChangesAsync();

        ValidationException name = await Assert.ThrowsAsync<ValidationException>(
            () => svc.AdminUpdateBookingAsync(1, new AdminUpdateBookingRequest { CustomerName = "Jane\u009b" }));
        ValidationException requests = await Assert.ThrowsAsync<ValidationException>(
            () => svc.AdminUpdateBookingAsync(1, new AdminUpdateBookingRequest { SpecialRequests = "x\u001b]0;t\u0007" }));

        Assert.Equal(ErrorCodes.BookingNameInvalid, name.Code);
        Assert.Equal(ErrorCodes.BookingSpecialRequestsInvalid, requests.Code);
        Assert.Equal("Jane", (await _db.Bookings.FindAsync(1))!.CustomerName);
    }

    [Fact]
    public async Task AdminUpdateBookingAsync_AcceptsMultilineSpecialRequests()
    {
        AdminService svc = CreateService();
        SeedBase(1);
        _db.Bookings.Add(new Booking { Id = 1, RestaurantId = 1, SectionId = 1, TableId = 1, Date = DateTime.UtcNow, BookingRef = "B1" });
        await _db.SaveChangesAsync();

        BookingDetailDto? result = await svc.AdminUpdateBookingAsync(1, new AdminUpdateBookingRequest { SpecialRequests = "nuts\nwindow\tseat" });

        Assert.Equal("nuts\nwindow\tseat", result!.SpecialRequests);
    }
}
