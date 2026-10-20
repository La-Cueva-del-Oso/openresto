using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.TestInfrastructure;

/// <summary>
/// Seeds the canonical Restaurant+Section+Table graph used across service tests.
/// Variant overloads match the two seeding shapes observed in the codebase.
/// </summary>
internal static class TestSeed
{
    /// <summary>
    /// Single 4-seat table (BookingService shape). Restaurant has no opening hours set.
    /// </summary>
    public static void BasicRestaurant(AppDbContext db)
    {
        db.Restaurants.Add(new Restaurant { Id = 1, Name = "Test Restaurant" });
        db.Sections.Add(new Section { Id = 1, Name = "Main", RestaurantId = 1 });
        db.Tables.Add(new Table { Id = 1, Name = "T1", Seats = 4, SectionId = 1 });
        db.SaveChanges();
    }

    /// <summary>
    /// Opens every location still on the default hours around the clock (equal open and close is
    /// "always open"), so a booking dated relative to <c>UtcNow</c> does not hit the opening-hours
    /// rule whenever the suite happens to run outside 09:00-22:00. A location with hours of its
    /// own — the ones that test the rule — is left alone.
    /// </summary>
    public static void OpenAllDay(AppDbContext db)
    {
        foreach (Restaurant r in db.Restaurants.Where(r => r.OpenTime == OpeningHourDefaults.Open && r.CloseTime == OpeningHourDefaults.Close).ToList())
        {
            r.OpenTime = "00:00";
            r.CloseTime = "00:00";
        }

        db.SaveChanges();
    }

    /// <summary>
    /// Restaurant with OpenTime/CloseTime/Timezone set, plus two tables (Availability shape).
    /// Defaults to the 11:00-13:00 UTC window used by availability slot tests.
    /// </summary>
    public static void RestaurantWithHours(
        AppDbContext db,
        string open = "11:00",
        string close = "13:00",
        string tz = "UTC")
    {
        db.Restaurants.Add(new Restaurant
        {
            Id = 1, Name = "Test", OpenTime = open, CloseTime = close, Timezone = tz
        });
        db.Sections.Add(new Section { Id = 1, Name = "Main", RestaurantId = 1 });
        db.Tables.Add(new Table { Id = 1, Name = "T1", Seats = 2, SectionId = 1 });
        db.Tables.Add(new Table { Id = 2, Name = "T2", Seats = 4, SectionId = 1 });
        db.SaveChanges();
    }
}
