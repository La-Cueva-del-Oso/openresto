using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// A purge is the GDPR erasure path, so it has to take the guest's name, address and reference
/// out of every table that copied them, not just the booking row.
/// </summary>
public partial class AdminServiceTests
{
    private const string GoneRef = "gone-ref-0001";
    private const string GoneName = "Purged Guest";
    private const string GoneEmail = "purged@example.com";
    private const string KeptRef = "kept-ref-0002";

    private async Task SeedGuestTracesAsync(int bookingId, string bookingRef, string name, string email)
    {
        _db.Bookings.Add(new Booking
        {
            Id = bookingId,
            RestaurantId = 1,
            SectionId = 1,
            TableId = 1,
            Date = DateTime.UtcNow.AddDays(1),
            BookingRef = bookingRef,
            CustomerName = name,
            CustomerEmail = email,
        });
        await _db.SaveChangesAsync();

        _db.AdminNotifications.Add(new AdminNotification
        {
            RestaurantId = 1,
            BookingId = bookingId,
            BookingRef = bookingRef,
            Type = "BookingCreated",
            CustomerName = name,
            CreatedAt = DateTime.UtcNow,
        });
        // A row whose FK was already nulled (SET NULL on an earlier delete) still names the guest.
        _db.AdminNotifications.Add(new AdminNotification
        {
            RestaurantId = 1,
            BookingRef = bookingRef,
            Type = "BookingCancelled",
            CustomerName = name,
            CreatedAt = DateTime.UtcNow,
        });
        _db.EmailFailures.Add(new EmailFailure { BookingRef = bookingRef, RecipientEmail = email, ErrorMessage = "x" });
        _db.WaitlistEntries.Add(new WaitlistEntry
        {
            RestaurantId = 1,
            Ref = "w-" + bookingRef,
            Name = name,
            Email = email,
            BookingId = bookingId,
            Status = WaitlistStatus.Seated,
            CreatedAt = DateTime.UtcNow,
        });
        _db.GuestPushSubscriptions.Add(new GuestPushSubscription
        {
            BookingId = bookingId,
            Channel = GuestPushChannels.Expo,
            Endpoint = "ExponentPushToken[" + bookingRef + "]",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task SeedTwoGuestsAsync()
    {
        SeedBase(1);
        await SeedGuestTracesAsync(1, GoneRef, GoneName, GoneEmail);
        await SeedGuestTracesAsync(2, KeptRef, "Kept Guest", "kept@example.com");
        // Capacity notifications carry no ref; an empty-ref match must not sweep them up.
        _db.AdminNotifications.Add(new AdminNotification
        {
            RestaurantId = 1,
            Type = NotificationType.RestaurantNearlyFull,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private async Task AssertNothingLeftOfAsync(string bookingRef, string name, string email)
    {
        Assert.False(await _db.Bookings.AnyAsync(b => b.BookingRef == bookingRef || b.CustomerEmail == email || b.CustomerName == name), "Bookings");
        Assert.False(await _db.AdminNotifications.AnyAsync(n => n.BookingRef == bookingRef || n.CustomerName == name), "AdminNotifications");
        Assert.False(await _db.EmailFailures.AnyAsync(f => f.BookingRef == bookingRef || f.RecipientEmail == email), "EmailFailures");
        Assert.False(await _db.WaitlistEntries.AnyAsync(w => w.Email == email || w.Name == name), "WaitlistEntries");
        Assert.False(await _db.GuestPushSubscriptions.AnyAsync(s => s.Endpoint.Contains(bookingRef)), "GuestPushSubscriptions");
    }

    [Fact]
    public async Task PurgeBookingAsync_LeavesNoTraceOfTheGuestInAnyTable()
    {
        AdminService svc = CreateService();
        await SeedTwoGuestsAsync();

        Assert.True(await svc.PurgeBookingAsync(1));

        await AssertNothingLeftOfAsync(GoneRef, GoneName, GoneEmail);
    }

    [Fact]
    public async Task PurgeBookingAsync_KeepsEveryOtherGuestsRows()
    {
        AdminService svc = CreateService();
        await SeedTwoGuestsAsync();

        await svc.PurgeBookingAsync(1);

        Assert.True(await _db.Bookings.AnyAsync(b => b.BookingRef == KeptRef));
        Assert.Equal(2, await _db.AdminNotifications.CountAsync(n => n.BookingRef == KeptRef));
        Assert.True(await _db.EmailFailures.AnyAsync(f => f.BookingRef == KeptRef));
        Assert.True(await _db.WaitlistEntries.AnyAsync(w => w.Email == "kept@example.com"));
        Assert.True(await _db.GuestPushSubscriptions.AnyAsync(s => s.Endpoint.Contains(KeptRef)));
        Assert.True(await _db.AdminNotifications.AnyAsync(n => n.Type == NotificationType.RestaurantNearlyFull));
    }

    /// <summary>
    /// <c>DELETE /api/bookings/{id}</c> reaches the same repository method, so it erases the same set.
    /// </summary>
    [Fact]
    public async Task BookingRepositoryDelete_LeavesNoTraceOfTheGuestInAnyTable()
    {
        await SeedTwoGuestsAsync();

        await new BookingRepository(_db).DeleteAsync(1);

        await AssertNothingLeftOfAsync(GoneRef, GoneName, GoneEmail);
        Assert.True(await _db.Bookings.AnyAsync(b => b.BookingRef == KeptRef));
    }
}
