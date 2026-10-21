using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Application.Settings;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;
using OpenRestoApi.Infrastructure.Persistence.Repositories;

namespace OpenRestoApi.Tests.Services;

/// <summary>
/// Opt-in, opt-out and the reminder pass, over the real repositories: a guest is the
/// reference-plus-email pair and a miss on either half answers the same false; a device is
/// stored once per booking; and a pass sends each lead once, drops addresses that are gone,
/// keeps addresses that merely failed, and prunes subscriptions whose sitting has passed.
/// </summary>
public class GuestReminderServiceTests : IDisposable
{
    private const string Ref = "crispy-basil-truffle";
    private const string Email = "guest@example.com";
    private const string Endpoint = "ExponentPushToken[device-1]";
    private const string WebsiteUrl = "https://bookings.example.com";

    private static readonly DateTime Now = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow { get; set; } = Now;
    }

    private readonly AppDbContext _db = TestDbFactory.Create(Guid.NewGuid().ToString());
    private readonly FixedClock _clock = new();
    private readonly Mock<IGuestPushSender> _sender = new();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private GuestReminderService CreateService(GuestPushSettings? settings = null)
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c[It.IsAny<string>()]).Returns(string.Empty);
        config.Setup(c => c["Website:Url"]).Returns(WebsiteUrl);

        return new GuestReminderService(
            new BookingRepository(_db),
            new GuestPushSubscriptionRepository(_db),
            _sender.Object,
            new BrandService(new BrandSettingsRepository(_db), config.Object),
            Options.Create(settings ?? new GuestPushSettings()),
            _clock,
            NullLogger<GuestReminderService>.Instance);
    }

    private Booking SeedBooking(
        DateTime dateUtc,
        string bookingRef = Ref,
        bool cancelled = false,
        string timezone = "UTC",
        int seats = 2)
    {
        var restaurant = new Restaurant { Name = "Bistro", Timezone = timezone };
        _db.Restaurants.Add(restaurant);
        var booking = new Booking
        {
            Restaurant = restaurant,
            Date = dateUtc,
            EndTime = dateUtc.AddHours(2),
            CustomerEmail = Email,
            CustomerName = "Guest",
            Seats = seats,
            BookingRef = bookingRef,
            IsCancelled = cancelled,
        };
        _db.Bookings.Add(booking);
        _db.SaveChanges();
        return booking;
    }

    private GuestPushSubscription SeedSubscription(
        Booking booking,
        DateTime createdAt,
        string endpoint = Endpoint,
        string locale = "en",
        int? lastReminderLeadHours = null)
    {
        var subscription = new GuestPushSubscription
        {
            BookingId = booking.Id,
            Channel = GuestPushChannels.Expo,
            Endpoint = endpoint,
            Locale = locale,
            CreatedAt = createdAt,
            LastReminderLeadHours = lastReminderLeadHours,
        };
        _db.GuestPushSubscriptions.Add(subscription);
        _db.SaveChanges();
        return subscription;
    }

    private static GuestReminderSubscribeRequest ExpoRequest(string endpoint = Endpoint, string? locale = null) => new()
    {
        Email = Email,
        Channel = GuestPushChannels.Expo,
        Endpoint = endpoint,
        Locale = locale,
    };

    private static GuestReminderSubscribeRequest WebPushRequest(string? p256dh, string? auth) => new()
    {
        Email = Email,
        Channel = GuestPushChannels.WebPush,
        Endpoint = "https://push.example/sub1",
        P256dh = p256dh,
        Auth = auth,
    };

    private void SenderAnswers(GuestPushResult result) =>
        _sender.Setup(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

    private Task<List<GuestPushSubscription>> StoredAsync() => _db.GuestPushSubscriptions.AsNoTracking().ToListAsync();

    // ---- SubscribeAsync ----

    [Fact]
    public async Task SubscribeAsync_ReturnsFalseForAnUnknownRefOrWrongEmail()
    {
        SeedBooking(Now.AddDays(3));
        GuestReminderService service = CreateService();

        bool unknownRef = await service.SubscribeAsync("no-such-ref", Email, ExpoRequest());
        bool wrongEmail = await service.SubscribeAsync(Ref, "stranger@example.com", ExpoRequest());

        Assert.False(unknownRef);
        Assert.False(wrongEmail);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task SubscribeAsync_MatchesTheEmailCaseInsensitivelyAndTrimmed()
    {
        SeedBooking(Now.AddDays(3));

        bool ok = await CreateService().SubscribeAsync(Ref, "  GUEST@Example.com ", ExpoRequest());

        Assert.True(ok);
        Assert.Single(await StoredAsync());
    }

    [Fact]
    public async Task SubscribeAsync_RejectsAnUnknownChannel()
    {
        SeedBooking(Now.AddDays(3));
        var request = new GuestReminderSubscribeRequest { Email = Email, Channel = "sms", Endpoint = "+15550100" };

        var ex = await Assert.ThrowsAsync<ValidationException>(() => CreateService().SubscribeAsync(Ref, Email, request));

        Assert.Equal(ErrorCodes.BookingReminderChannelInvalid, ex.Code);
        Assert.Empty(await StoredAsync());
    }

    [Theory]
    [InlineData("p256dh-key", null)]
    [InlineData(null, "auth-secret")]
    [InlineData("  ", "auth-secret")]
    public async Task SubscribeAsync_RequiresKeysOnTheWebPushChannel(string? p256dh, string? auth)
    {
        SeedBooking(Now.AddDays(3));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CreateService().SubscribeAsync(Ref, Email, WebPushRequest(p256dh, auth)));

        Assert.Equal(ErrorCodes.BookingReminderKeysRequired, ex.Code);
        Assert.Empty(await StoredAsync());
    }

    [Theory]
    [InlineData(GuestPushChannels.Expo, "https://fcm.googleapis.com/fcm/send/abc")]
    [InlineData(GuestPushChannels.Expo, "not-a-token")]
    [InlineData(GuestPushChannels.WebPush, "http://push.example/sub1")]
    [InlineData(GuestPushChannels.WebPush, "https://10.0.0.7/sub1")]
    [InlineData(GuestPushChannels.WebPush, "https://localhost/sub1")]
    [InlineData(GuestPushChannels.WebPush, "ExponentPushToken[device-1]")]
    public async Task SubscribeAsync_RejectsAnAddressTheServerWouldNotSendTo(string channel, string endpoint)
    {
        SeedBooking(Now.AddDays(3));
        var request = new GuestReminderSubscribeRequest
        {
            Email = Email,
            Channel = channel,
            Endpoint = endpoint,
            P256dh = "p256dh-key",
            Auth = "auth-secret",
        };

        var ex = await Assert.ThrowsAsync<ValidationException>(() => CreateService().SubscribeAsync(Ref, Email, request));

        Assert.Equal(ErrorCodes.BookingReminderEndpointInvalid, ex.Code);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task SubscribeAsync_StoresBothWebPushKeysWhenPresent()
    {
        SeedBooking(Now.AddDays(3));

        bool ok = await CreateService().SubscribeAsync(Ref, Email, WebPushRequest("p256dh-key", "auth-secret"));

        Assert.True(ok);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Equal(GuestPushChannels.WebPush, stored.Channel);
        Assert.Equal("https://push.example/sub1", stored.Endpoint);
        Assert.Equal("p256dh-key", stored.P256dh);
        Assert.Equal("auth-secret", stored.Auth);
    }

    [Fact]
    public async Task SubscribeAsync_RejectsACancelledBooking()
    {
        SeedBooking(Now.AddDays(3), cancelled: true);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => CreateService().SubscribeAsync(Ref, Email, ExpoRequest()));

        Assert.Equal(ErrorCodes.BookingReminderCancelled, ex.Code);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task SubscribeAsync_RejectsABookingThatHasStarted()
    {
        SeedBooking(Now, bookingRef: "started-now");
        SeedBooking(Now.AddMinutes(1), bookingRef: "starts-in-a-minute");
        GuestReminderService service = CreateService();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.SubscribeAsync("started-now", Email, ExpoRequest()));
        bool stillAhead = await service.SubscribeAsync("starts-in-a-minute", Email, ExpoRequest());

        Assert.Equal(ErrorCodes.BookingReminderTooLate, ex.Code);
        Assert.True(stillAhead);
        Assert.Single(await StoredAsync());
    }

    [Fact]
    public async Task SubscribeAsync_StoresTheSubscription()
    {
        Booking booking = SeedBooking(Now.AddDays(3));

        bool ok = await CreateService().SubscribeAsync(Ref, Email, ExpoRequest(endpoint: $"  {Endpoint}  ", locale: "FR"));

        Assert.True(ok);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Equal(booking.Id, stored.BookingId);
        Assert.Equal(GuestPushChannels.Expo, stored.Channel);
        Assert.Equal(Endpoint, stored.Endpoint);
        Assert.Null(stored.P256dh);
        Assert.Null(stored.Auth);
        Assert.Equal("fr", stored.Locale);
        Assert.Equal(Now, stored.CreatedAt);
        Assert.Null(stored.LastReminderLeadHours);
        Assert.Null(stored.LastReminderSentAt);
    }

    [Theory]
    [InlineData("pt")]
    [InlineData("")]
    [InlineData(null)]
    public async Task SubscribeAsync_StoresTheDefaultLocaleForAnUnsupportedOne(string? locale)
    {
        SeedBooking(Now.AddDays(3));

        await CreateService().SubscribeAsync(Ref, Email, ExpoRequest(locale: locale));

        Assert.Equal(SupportedLocales.Default, Assert.Single(await StoredAsync()).Locale);
    }

    [Fact]
    public async Task SubscribeAsync_UpdatesAnExistingDeviceInsteadOfDuplicating()
    {
        SeedBooking(Now.AddDays(3));
        GuestReminderService service = CreateService();
        await service.SubscribeAsync(Ref, Email, ExpoRequest(locale: "en"));
        _clock.UtcNow = Now.AddHours(1);

        bool again = await service.SubscribeAsync(Ref, Email, ExpoRequest(locale: "de"));

        Assert.True(again);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Equal("de", stored.Locale);
        Assert.Equal(Now, stored.CreatedAt);
    }

    [Fact]
    public async Task SubscribeAsync_KeepsOneRowPerDevice()
    {
        SeedBooking(Now.AddDays(3));
        GuestReminderService service = CreateService();

        await service.SubscribeAsync(Ref, Email, ExpoRequest(endpoint: "ExponentPushToken[phone]"));
        await service.SubscribeAsync(Ref, Email, ExpoRequest(endpoint: "ExponentPushToken[tablet]"));

        Assert.Equal(2, (await StoredAsync()).Count);
    }

    [Fact]
    public async Task SubscribeAsync_AcceptsTheLastDeviceUnderTheCap()
    {
        var settings = new GuestPushSettings();
        Booking booking = SeedBooking(Now.AddDays(3));
        Booking other = SeedBooking(Now.AddDays(3), bookingRef: "another-booking");
        for (int i = 0; i < settings.MaxDevicesPerBooking; i++)
        {
            SeedSubscription(other, Now, endpoint: $"ExponentPushToken[other-{i}]");
        }

        for (int i = 0; i < settings.MaxDevicesPerBooking - 1; i++)
        {
            SeedSubscription(booking, Now, endpoint: $"ExponentPushToken[device-{i}]");
        }

        bool ok = await CreateService().SubscribeAsync(Ref, Email, ExpoRequest(endpoint: "ExponentPushToken[last]"));

        Assert.True(ok);
        Assert.Equal(settings.MaxDevicesPerBooking, (await StoredAsync()).Count(s => s.BookingId == booking.Id));
    }

    [Fact]
    public async Task SubscribeAsync_RefusesADeviceOverTheCap()
    {
        var settings = new GuestPushSettings();
        Booking booking = SeedBooking(Now.AddDays(3));
        for (int i = 0; i < settings.MaxDevicesPerBooking; i++)
        {
            SeedSubscription(booking, Now, endpoint: $"ExponentPushToken[device-{i}]");
        }

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => CreateService().SubscribeAsync(Ref, Email, ExpoRequest(endpoint: "ExponentPushToken[one-too-many]")));

        Assert.Equal(ErrorCodes.BookingReminderDeviceLimit, ex.Code);
        Assert.Equal(settings.MaxDevicesPerBooking, (await StoredAsync()).Count);
    }

    [Fact]
    public async Task SubscribeAsync_ReRegistersAKnownDeviceEvenAtTheCap()
    {
        var settings = new GuestPushSettings();
        Booking booking = SeedBooking(Now.AddDays(3));
        for (int i = 0; i < settings.MaxDevicesPerBooking; i++)
        {
            SeedSubscription(booking, Now, endpoint: $"ExponentPushToken[device-{i}]", locale: "en");
        }

        bool ok = await CreateService().SubscribeAsync(Ref, Email, ExpoRequest(endpoint: "ExponentPushToken[device-0]", locale: "de"));

        Assert.True(ok);
        List<GuestPushSubscription> stored = await StoredAsync();
        Assert.Equal(settings.MaxDevicesPerBooking, stored.Count);
        Assert.Equal("de", stored.Single(s => s.Endpoint == "ExponentPushToken[device-0]").Locale);
    }

    // ---- UnsubscribeAsync ----

    [Fact]
    public async Task UnsubscribeAsync_ReturnsFalseForAnUnknownRefOrWrongEmail()
    {
        Booking booking = SeedBooking(Now.AddDays(3));
        SeedSubscription(booking, Now.AddHours(-1));
        GuestReminderService service = CreateService();

        bool unknownRef = await service.UnsubscribeAsync("no-such-ref", Email, Endpoint);
        bool wrongEmail = await service.UnsubscribeAsync(Ref, "stranger@example.com", Endpoint);

        Assert.False(unknownRef);
        Assert.False(wrongEmail);
        Assert.Single(await StoredAsync());
    }

    [Fact]
    public async Task UnsubscribeAsync_RemovesOnlyThatDevice()
    {
        Booking booking = SeedBooking(Now.AddDays(3));
        SeedSubscription(booking, Now.AddHours(-1), endpoint: "ExponentPushToken[phone]");
        SeedSubscription(booking, Now.AddHours(-1), endpoint: "ExponentPushToken[tablet]");

        bool ok = await CreateService().UnsubscribeAsync(Ref, Email, " ExponentPushToken[phone] ");

        Assert.True(ok);
        GuestPushSubscription remaining = Assert.Single(await StoredAsync());
        Assert.Equal("ExponentPushToken[tablet]", remaining.Endpoint);
    }

    [Fact]
    public async Task UnsubscribeAsync_IsIdempotent()
    {
        Booking booking = SeedBooking(Now.AddDays(3));
        SeedSubscription(booking, Now.AddHours(-1));
        GuestReminderService service = CreateService();

        bool first = await service.UnsubscribeAsync(Ref, Email, Endpoint);
        bool second = await service.UnsubscribeAsync(Ref, Email, Endpoint);
        bool neverSubscribed = await service.UnsubscribeAsync(Ref, Email, "ExponentPushToken[other]");

        Assert.True(first);
        Assert.True(second);
        Assert.True(neverSubscribed);
        Assert.Empty(await StoredAsync());
    }

    // ---- SendDueRemindersAsync ----

    [Fact]
    public async Task SendDueRemindersAsync_DeliversWhenAWindowOpens_AndRecordsTheLead()
    {
        Booking booking = SeedBooking(Now.AddHours(24));
        SeedSubscription(booking, Now.AddHours(-1));
        SenderAnswers(GuestPushResult.Delivered);

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(1, delivered);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Equal(24, stored.LastReminderLeadHours);
        Assert.Equal(Now, stored.LastReminderSentAt);
        _sender.Verify(s => s.SendAsync(
            It.Is<GuestPushAddress>(to => to.Endpoint == Endpoint),
            It.Is<GuestPushMessage>(m =>
                m.Tag == $"booking-{booking.Id}" &&
                m.Url == $"{WebsiteUrl}/booking-confirmation/{Ref}?email=guest%40example.com"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SendDueRemindersAsync_DoesNothingBeforeTheWindow()
    {
        Booking booking = SeedBooking(Now.AddHours(25));
        SeedSubscription(booking, Now.AddHours(-1));

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(0, delivered);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Null(stored.LastReminderLeadHours);
        Assert.Null(stored.LastReminderSentAt);
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendDueRemindersAsync_SkipsAWindowThatOpenedBeforeTheGuestOptedIn()
    {
        Booking booking = SeedBooking(Now.AddHours(20));
        SeedSubscription(booking, Now.AddMinutes(-5));

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(0, delivered);
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendDueRemindersAsync_RemovesAStaleAddress()
    {
        Booking booking = SeedBooking(Now.AddHours(24));
        SeedSubscription(booking, Now.AddHours(-1));
        SenderAnswers(GuestPushResult.Stale);

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(0, delivered);
        Assert.Empty(await StoredAsync());
    }

    [Fact]
    public async Task SendDueRemindersAsync_KeepsAFailedAddressButDoesNotRetryTheSameLead()
    {
        Booking booking = SeedBooking(Now.AddHours(24));
        SeedSubscription(booking, Now.AddHours(-1));
        SenderAnswers(GuestPushResult.Failed("HTTP 500: push failed"));
        GuestReminderService service = CreateService();

        int firstPass = await service.SendDueRemindersAsync();
        int secondPass = await service.SendDueRemindersAsync();

        Assert.Equal(0, firstPass);
        Assert.Equal(0, secondPass);
        GuestPushSubscription stored = Assert.Single(await StoredAsync());
        Assert.Equal(24, stored.LastReminderLeadHours);
        Assert.Equal(Now, stored.LastReminderSentAt);
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Once);

        // The next lead window still gets its own attempt.
        _clock.UtcNow = Now.AddHours(22);
        await service.SendDueRemindersAsync();

        Assert.Equal(2, Assert.Single(await StoredAsync()).LastReminderLeadHours);
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SendDueRemindersAsync_SendsEachLeadOnce()
    {
        Booking booking = SeedBooking(Now.AddHours(24));
        SeedSubscription(booking, Now.AddHours(-1));
        SenderAnswers(GuestPushResult.Delivered);
        GuestReminderService service = CreateService();

        int atTwentyFour = await service.SendDueRemindersAsync();
        _clock.UtcNow = Now.AddHours(1);
        int anHourLater = await service.SendDueRemindersAsync();
        _clock.UtcNow = Now.AddHours(22);
        int atTwo = await service.SendDueRemindersAsync();
        int atTwoAgain = await service.SendDueRemindersAsync();

        Assert.Equal(1, atTwentyFour);
        Assert.Equal(0, anHourLater);
        Assert.Equal(1, atTwo);
        Assert.Equal(0, atTwoAgain);
        Assert.Equal(2, Assert.Single(await StoredAsync()).LastReminderLeadHours);
    }

    private void SeedDueSubscriptions(int count)
    {
        // Every sitting is inside its 24h window and every guest opted in an hour ago.
        for (int i = 0; i < count; i++)
        {
            Booking booking = SeedBooking(Now.AddHours(24).AddMinutes(-i), bookingRef: $"due-{i}");
            SeedSubscription(booking, Now.AddHours(-1), endpoint: $"ExponentPushToken[due-{i}]");
        }
    }

    [Fact]
    public async Task SendDueRemindersAsync_SendsTheCapAndLeavesTheRestForTheNextPass()
    {
        var settings = new GuestPushSettings { MaxSendsPerPass = 3 };
        SeedDueSubscriptions(settings.MaxSendsPerPass + 1);
        SenderAnswers(GuestPushResult.Delivered);
        GuestReminderService service = CreateService(settings);

        int firstPass = await service.SendDueRemindersAsync();

        Assert.Equal(3, firstPass);
        Assert.Single((await StoredAsync()).Where(s => s.LastReminderLeadHours is null));
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(3));

        int secondPass = await service.SendDueRemindersAsync();

        Assert.Equal(1, secondPass);
        Assert.All(await StoredAsync(), s => Assert.Equal(24, s.LastReminderLeadHours));
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Fact]
    public async Task SendDueRemindersAsync_LeavesNothingPendingWhenDueEqualsTheCap()
    {
        var settings = new GuestPushSettings { MaxSendsPerPass = 3 };
        SeedDueSubscriptions(settings.MaxSendsPerPass);
        SenderAnswers(GuestPushResult.Delivered);
        GuestReminderService service = CreateService(settings);

        int firstPass = await service.SendDueRemindersAsync();
        int secondPass = await service.SendDueRemindersAsync();

        Assert.Equal(3, firstPass);
        Assert.Equal(0, secondPass);
        Assert.All(await StoredAsync(), s => Assert.Equal(24, s.LastReminderLeadHours));
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task SendDueRemindersAsync_PrunesSpentSubscriptions()
    {
        Booking started = SeedBooking(Now.AddHours(-1), bookingRef: "started");
        Booking cancelled = SeedBooking(Now.AddHours(30), bookingRef: "cancelled", cancelled: true);
        Booking upcoming = SeedBooking(Now.AddHours(30), bookingRef: "upcoming");
        SeedSubscription(started, Now.AddDays(-2), endpoint: "ExponentPushToken[started]");
        SeedSubscription(cancelled, Now.AddDays(-2), endpoint: "ExponentPushToken[cancelled]");
        SeedSubscription(upcoming, Now.AddDays(-2), endpoint: "ExponentPushToken[upcoming]");

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(0, delivered);
        GuestPushSubscription remaining = Assert.Single(await StoredAsync());
        Assert.Equal("ExponentPushToken[upcoming]", remaining.Endpoint);
        _sender.Verify(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SendDueRemindersAsync_WritesTheReminderInTheGuestsLocaleAndTimezone()
    {
        // 23:30Z on 11 Sep is 19:30 in Toronto (EDT, UTC-4); "now" is the same local time the
        // evening before, so the 24h window has just opened and the sitting is "tomorrow".
        var sittingUtc = new DateTime(2026, 9, 11, 23, 30, 0, DateTimeKind.Utc);
        _clock.UtcNow = sittingUtc.AddHours(-24);
        Booking booking = SeedBooking(sittingUtc, timezone: "America/Toronto", seats: 3);
        SeedSubscription(booking, _clock.UtcNow.AddHours(-1), locale: "fr");
        GuestPushMessage? pushed = null;
        _sender
            .Setup(s => s.SendAsync(It.IsAny<GuestPushAddress>(), It.IsAny<GuestPushMessage>(), It.IsAny<CancellationToken>()))
            .Callback<GuestPushAddress, GuestPushMessage, CancellationToken>((_, message, _) => pushed = message)
            .ReturnsAsync(GuestPushResult.Delivered);

        int delivered = await CreateService().SendDueRemindersAsync();

        Assert.Equal(1, delivered);
        Assert.NotNull(pushed);
        Assert.Equal("Votre table chez Bistro", pushed.Title);
        Assert.Equal($"Demain à 19:30 · 3 convives · Réf. {Ref}", pushed.Body);
        Assert.Equal($"{WebsiteUrl}/booking-confirmation/{Ref}?email=guest%40example.com", pushed.Url);
    }
}
