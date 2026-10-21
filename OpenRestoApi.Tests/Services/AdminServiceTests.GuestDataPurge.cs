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
            Ref = $"w-{bookingId}-{bookingRef}",
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

    private const string SharedRef = "same-ref-0003";
    private const string OtherName = "Other Guest";
    private const string OtherEmail = "other@example.com";

    /// <summary>
    /// Nothing checks a drawn reference against the table, so two bookings can carry the same one.
    /// Purging one must erase that guest's rows (found by the link they have, or by the reference
    /// together with their own name or address) and leave the other booking's untouched.
    /// </summary>
    [Fact]
    public async Task PurgeBookingAsync_WithASharedReference_ErasesOnlyThatGuestsRows()
    {
        AdminService svc = CreateService();
        SeedBase(1);
        await SeedGuestTracesAsync(1, SharedRef, GoneName, GoneEmail);
        await SeedGuestTracesAsync(2, SharedRef, OtherName, OtherEmail);

        Assert.True(await svc.PurgeBookingAsync(1));

        Assert.False(await _db.Bookings.AnyAsync(b => b.Id == 1), "Bookings");
        Assert.False(await _db.AdminNotifications.AnyAsync(n => n.CustomerName == GoneName), "AdminNotifications");
        Assert.False(await _db.EmailFailures.AnyAsync(f => f.RecipientEmail == GoneEmail), "EmailFailures");
        Assert.False(await _db.WaitlistEntries.AnyAsync(w => w.BookingId == 1), "WaitlistEntries");

        Assert.True(await _db.Bookings.AnyAsync(b => b.Id == 2));
        Assert.Equal(2, await _db.AdminNotifications.CountAsync(n => n.CustomerName == OtherName));
        Assert.True(await _db.EmailFailures.AnyAsync(f => f.RecipientEmail == OtherEmail));
        Assert.True(await _db.WaitlistEntries.AnyAsync(w => w.BookingId == 2));
        Assert.True(await _db.GuestPushSubscriptions.AnyAsync(s => s.BookingId == 2));
    }

    /// <summary>
    /// A booking with no reference (an admin-recorded one can have none) must not match the
    /// capacity notifications and failures that also carry an empty one.
    /// </summary>
    [Fact]
    public async Task PurgeBookingAsync_OfABookingWithNoReference_KeepsRowsThatHaveNoReferenceEither()
    {
        AdminService svc = CreateService();
        SeedBase(1);
        _db.Bookings.Add(new Booking
        {
            Id = 1,
            RestaurantId = 1,
            SectionId = 1,
            TableId = 1,
            Date = DateTime.UtcNow.AddDays(1),
            BookingRef = string.Empty,
            CustomerName = GoneName,
        });
        _db.AdminNotifications.Add(new AdminNotification
        {
            RestaurantId = 1,
            Type = NotificationType.RestaurantNearlyFull,
            CreatedAt = DateTime.UtcNow,
        });
        _db.EmailFailures.Add(new EmailFailure { BookingRef = string.Empty, RecipientEmail = OtherEmail, ErrorMessage = "x" });
        await _db.SaveChangesAsync();

        Assert.True(await svc.PurgeBookingAsync(1));

        Assert.False(await _db.Bookings.AnyAsync(b => b.Id == 1));
        Assert.True(await _db.AdminNotifications.AnyAsync(n => n.Type == NotificationType.RestaurantNearlyFull));
        Assert.True(await _db.EmailFailures.AnyAsync(f => f.RecipientEmail == OtherEmail));
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
