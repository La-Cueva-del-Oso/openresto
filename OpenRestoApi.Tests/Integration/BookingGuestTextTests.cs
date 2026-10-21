using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.Integration;

/// <seealso>GuestTextTests.CustomerName_RejectsEveryControlCharacter</seealso>
/// <seealso>GuestTextTests.SpecialRequests_AllowsLineBreaksAndTabsOnly</seealso>
public class BookingGuestTextTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory = factory;

    private async Task<HttpResponseMessage> PostBookingAsync(string field, string value, int daysAhead)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Restaurant restaurant = db.Restaurants.First();
        Section section = db.Sections.First(s => s.RestaurantId == restaurant.Id);
        Table table = db.Tables.First(t => t.SectionId == section.Id);

        var body = new Dictionary<string, object>
        {
            ["restaurantId"] = restaurant.Id,
            ["sectionId"] = section.Id,
            ["tableId"] = table.Id,
            ["date"] = DateTime.UtcNow.AddDays(daysAhead).ToString("yyyy-MM-ddT12:00:00"),
            ["customerEmail"] = "guesttext@test.com",
            ["seats"] = 2,
            [field] = value,
        };
        return await _factory.CreateClient().PostAsJsonAsync("/api/bookings", body);
    }

    [Theory]
    [InlineData("customerName", "Ada\u001b[2JLovelace", ErrorCodes.BookingNameInvalid)]
    [InlineData("customerName", "Ada\u001b]0;title\u0007", ErrorCodes.BookingNameInvalid)]
    [InlineData("specialRequests", "nuts\u001b[2J", ErrorCodes.BookingSpecialRequestsInvalid)]
    [InlineData("specialRequests", "nuts\u009b2J", ErrorCodes.BookingSpecialRequestsInvalid)]
    public async Task CreateBooking_RejectsControlCharacters_With400AndTheFieldCode(string field, string value, string code)
    {
        HttpResponseMessage response = await PostBookingAsync(field, value, daysAhead: 70);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("customerName", "José Núñez 🐻", 71)]
    [InlineData("specialRequests", "nut allergy\nwindow table, please\tthanks", 72)]
    public async Task CreateBooking_AcceptsOrdinaryTextAndFreeTextLineBreaks(string field, string value, int daysAhead)
    {
        HttpResponseMessage response = await PostBookingAsync(field, value, daysAhead);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
