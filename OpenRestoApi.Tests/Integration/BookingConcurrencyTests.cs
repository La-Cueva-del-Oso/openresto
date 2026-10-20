using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.Integration;

/// <summary>
/// Every path that picks a table for a booking races the others, so each rule is pinned from both
/// sides: requests for the same table and time yield one winner, requests for different tables or
/// times all land. Real SQLite and the real pipeline, since only the process serving requests
/// holds the lock.
/// </summary>
public class BookingConcurrencyTests(BookingConcurrencyTests.FileDbFactory factory) : IClassFixture<BookingConcurrencyTests.FileDbFactory>
{
    private const int Racers = 8;
    private static readonly string Noon = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddT12:00:00");
    private static readonly string FourPm = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddT16:00:00");

    private readonly FileDbFactory _factory = factory;

    /// <summary>
    /// A file database gives each request its own connection, as in production. The shared
    /// in-memory connection of <see cref="TestWebAppFactory"/> funnels every request through one
    /// connection, which cannot carry concurrent requests at all.
    /// </summary>
    public sealed class FileDbFactory : TestWebAppFactory
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"openresto-race-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (ServiceDescriptor d in services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                        || d.ServiceType == typeof(DbContextOptions)
                        || d.ServiceType == typeof(AppDbContext)).ToList())
                {
                    services.Remove(d);
                }

                services.AddDbContext<AppDbContext>(options =>
                {
                    options.UseSqlite($"Data Source={_path}");
                    options.AddInterceptors(new SqlitePragmaInterceptor());
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                {
                    File.Delete(_path + suffix);
                }
            }
        }
    }

    private sealed record Floor(int RestaurantId, int SectionId, List<int> TableIds);

    private Floor SeedFloor(int tables, int? maxCoversPerSlot = null, int tableSeats = 4)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var restaurant = new Restaurant { Name = $"Race {Guid.NewGuid():N}", MaxCoversPerSlot = maxCoversPerSlot };
        var section = new Section { Name = "Main" };
        for (int i = 0; i < tables; i++)
        {
            section.Tables.Add(new Table { Name = $"T{i}", Seats = tableSeats });
        }
        restaurant.Sections.Add(section);
        db.Restaurants.Add(restaurant);
        db.SaveChanges();
        return new Floor(restaurant.Id, section.Id, section.Tables.Select(t => t.Id).ToList());
    }

    private static async Task<T[]> RaceAsync<T>(IEnumerable<Func<Task<T>>> calls)
    {
        var start = new TaskCompletionSource();
        Task<T>[] tasks = calls
            .Select(call => Task.Run(async () =>
            {
                await start.Task;
                return await call();
            }))
            .ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private Func<Task<HttpResponseMessage>> PublicBooking(Floor floor, int tableId, string date, string email, int seats = 2)
        => () => _factory.CreateClient().PostAsJsonAsync("/api/bookings", new
        {
            restaurantId = floor.RestaurantId,
            sectionId = floor.SectionId,
            tableId,
            date,
            customerEmail = email,
            seats,
        });

    private Func<Task<HttpResponseMessage>> AdminBooking(Floor floor, int tableId, string date, string email)
        => () => _factory.CreateAuthenticatedClient().PostAsJsonAsync("/api/admin/bookings", new
        {
            restaurantId = floor.RestaurantId,
            sectionId = floor.SectionId,
            tableId,
            date,
            customerEmail = email,
            seats = 2,
        });

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    private int ActiveBookingsOnTable(int tableId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.Bookings.Count(b => b.TableId == tableId && !b.IsCancelled);
    }

    [Fact]
    public async Task ConcurrentBookingsOfOneTable_OnlyOneIsCreated()
    {
        Floor floor = SeedFloor(tables: 1);

        HttpResponseMessage[] responses = await RaceAsync(
            Enumerable.Range(0, Racers).Select(i => PublicBooking(floor, floor.TableIds[0], Noon, $"guest{i}@test.com")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        HttpResponseMessage[] losers = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToArray();
        Assert.Equal(Racers - 1, losers.Length);
        foreach (HttpResponseMessage loser in losers)
        {
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal(ErrorCodes.BookingTableConflict, await CodeOf(loser));
        }
        Assert.Equal(1, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ConcurrentBookingsOfDifferentTablesOrTimes_AreAllCreated()
    {
        Floor floor = SeedFloor(tables: 4);
        var calls = new List<Func<Task<HttpResponseMessage>>>();
        foreach (int tableId in floor.TableIds)
        {
            calls.Add(PublicBooking(floor, tableId, Noon, $"noon{tableId}@test.com"));
        }
        calls.Add(PublicBooking(floor, floor.TableIds[0], FourPm, "later@test.com"));

        HttpResponseMessage[] responses = await RaceAsync(calls);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(2, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ARefusedBooking_DoesNotBlockTheNextOne()
    {
        Floor floor = SeedFloor(tables: 2);
        Assert.Equal(HttpStatusCode.Created, (await PublicBooking(floor, floor.TableIds[0], Noon, "first@test.com")()).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PublicBooking(floor, floor.TableIds[0], Noon, "second@test.com")()).StatusCode);

        Task<HttpResponseMessage> next = PublicBooking(floor, floor.TableIds[1], Noon, "third@test.com")();

        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(20))));
        Assert.Equal(HttpStatusCode.Created, (await next).StatusCode);
    }

    [Fact]
    public async Task AMalformedEmail_IsStillRefusedWithItsOwnCode()
    {
        Floor floor = SeedFloor(tables: 1);

        HttpResponseMessage response = await PublicBooking(floor, floor.TableIds[0], Noon, "not-an-email")();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.BookingEmailInvalid, await CodeOf(response));
        Assert.Equal(0, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ConcurrentPublicAndAdminBookingsOfOneTable_OnlyOneIsCreated()
    {
        Floor floor = SeedFloor(tables: 1);
        var calls = new List<Func<Task<HttpResponseMessage>>>();
        for (int i = 0; i < Racers / 2; i++)
        {
            calls.Add(PublicBooking(floor, floor.TableIds[0], Noon, $"guest{i}@test.com"));
            calls.Add(AdminBooking(floor, floor.TableIds[0], Noon, $"staff{i}@test.com"));
        }

        HttpResponseMessage[] responses = await RaceAsync(calls);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ConcurrentAdminBookingsOfDifferentTables_AreAllCreated()
    {
        Floor floor = SeedFloor(tables: 3);

        HttpResponseMessage[] responses = await RaceAsync(
            floor.TableIds.Select(id => AdminBooking(floor, id, Noon, $"staff{id}@test.com")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
    }

    [Fact]
    public async Task ConcurrentMovesOntoOneSlot_OnlyOneBookingMoves()
    {
        Floor floor = SeedFloor(tables: 1);
        int tableId = floor.TableIds[0];
        int[] ids = new int[2];
        string[] originalTimes = [Noon, DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-ddT20:00:00")];
        for (int i = 0; i < ids.Length; i++)
        {
            HttpResponseMessage created = await AdminBooking(floor, tableId, originalTimes[i], $"mover{i}@test.com")();
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ids[i] = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }

        HttpResponseMessage[] responses = await RaceAsync(ids.Select<int, Func<Task<HttpResponseMessage>>>(
            id => () => _factory.CreateAuthenticatedClient().PutAsJsonAsync($"/api/admin/bookings/{id}", new { date = FourPm + "Z" })));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        HttpResponseMessage loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(ErrorCodes.BookingMoveConflict, await CodeOf(loser));
    }

    [Fact]
    public async Task ConcurrentWaitlistSeatsOnOneTable_OnlyOnePartyIsSeated()
    {
        Floor floor = SeedFloor(tables: 1);
        int[] entryIds = SeedWaitlist(floor.RestaurantId, count: 2);

        HttpResponseMessage[] responses = await RaceAsync(entryIds.Select(SeatEntry));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        HttpResponseMessage loser = responses.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(ErrorCodes.WaitlistNoTableFree, await CodeOf(loser));
        Assert.Equal(1, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ConcurrentWaitlistSeatsOnSeparateTables_SeatEveryParty()
    {
        Floor floor = SeedFloor(tables: 2);
        int[] entryIds = SeedWaitlist(floor.RestaurantId, count: 2);

        HttpResponseMessage[] responses = await RaceAsync(entryIds.Select(SeatEntry));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(floor.TableIds, id => Assert.Equal(1, ActiveBookingsOnTable(id)));
    }

    [Fact]
    public async Task APublicBookingAndAWaitlistSeatOnOneTable_OnlyOneWins()
    {
        Floor floor = SeedFloor(tables: 1);
        int[] entryIds = SeedWaitlist(floor.RestaurantId, count: 1);
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:00");

        var calls = new List<Func<Task<HttpResponseMessage>>>
        {
            SeatEntry(entryIds[0]),
            AdminBooking(floor, floor.TableIds[0], now, "staff@test.com"),
        };
        HttpResponseMessage[] responses = await RaceAsync(calls);

        Assert.Equal(1, responses.Count(r => (int)r.StatusCode < 300));
        Assert.Equal(1, ActiveBookingsOnTable(floor.TableIds[0]));
    }

    [Fact]
    public async Task ConcurrentBookingsFillingTheCoverCap_AdmitOnlyWhatFits()
    {
        Floor floor = SeedFloor(tables: 2, maxCoversPerSlot: 4);

        HttpResponseMessage[] responses = await RaceAsync(
            floor.TableIds.Select((id, i) => PublicBooking(floor, id, Noon, $"cover{i}@test.com", seats: 3)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        HttpResponseMessage loser = responses.Single(r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(ErrorCodes.BookingPacingFull, await CodeOf(loser));
    }

    [Fact]
    public async Task ConcurrentBookingsWithinTheCoverCap_AreAllCreated()
    {
        Floor floor = SeedFloor(tables: 2, maxCoversPerSlot: 6);

        HttpResponseMessage[] responses = await RaceAsync(
            floor.TableIds.Select((id, i) => PublicBooking(floor, id, Noon, $"cover{i}@test.com", seats: 3)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
    }

    private Func<Task<HttpResponseMessage>> SeatEntry(int entryId)
        => () => _factory.CreateAuthenticatedClient().PostAsJsonAsync($"/api/admin/waitlist/{entryId}/seat", new { });

    private int[] SeedWaitlist(int restaurantId, int count)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entries = Enumerable.Range(1, count).Select(n => new WaitlistEntry
        {
            RestaurantId = restaurantId,
            Ref = Guid.NewGuid().ToString("N"),
            Number = n,
            Name = $"Party {n}",
            Seats = 2,
            Status = WaitlistStatus.Waiting,
            CreatedAt = DateTime.UtcNow,
        }).ToList();
        db.WaitlistEntries.AddRange(entries);
        db.SaveChanges();
        return entries.Select(e => e.Id).ToArray();
    }
}
