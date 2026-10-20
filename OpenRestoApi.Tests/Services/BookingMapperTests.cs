using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Mappings;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

public class BookingMapperTests
{
    private readonly BookingMapper _mapper = new();

    [Fact]
    public void ToNewEntity_IgnoresServerOwnedFields()
    {
        Booking booking = _mapper.ToNewEntity(new BookingDto
        {
            Id = 42,
            IsCancelled = true,
            CancelledAt = DateTime.UtcNow,
            BookingRef = "forged-ref",
            EndTime = DateTime.UtcNow.AddYears(1),
            RestaurantId = 1,
            Seats = 2
        });

        Assert.Equal(0, booking.Id);
        Assert.False(booking.IsCancelled);
        Assert.Null(booking.CancelledAt);
        Assert.Equal(string.Empty, booking.BookingRef);
        Assert.Null(booking.EndTime);
        Assert.Equal(BookingStatus.Booked, booking.Status);
    }

    [Fact]
    public void ToNewEntity_KeepsTheGuestSuppliedFields()
    {
        DateTime date = DateTime.UtcNow.AddDays(3);

        Booking booking = _mapper.ToNewEntity(new BookingDto
        {
            RestaurantId = 7,
            TableId = 3,
            SectionId = 2,
            Date = date,
            Seats = 4,
            CustomerName = "Ada",
            CustomerEmail = "ada@example.com",
            SpecialRequests = "Window"
        });

        Assert.Equal(7, booking.RestaurantId);
        Assert.Equal(3, booking.TableId);
        Assert.Equal(2, booking.SectionId);
        Assert.Equal(date, booking.Date);
        Assert.Equal(4, booking.Seats);
        Assert.Equal("Ada", booking.CustomerName);
        Assert.Equal("ada@example.com", booking.CustomerEmail);
        Assert.Equal("Window", booking.SpecialRequests);
    }
}
