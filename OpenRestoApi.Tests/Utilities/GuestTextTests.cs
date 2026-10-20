using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Utilities;

namespace OpenRestoApi.Tests.Utilities;

public class GuestTextTests
{
    [Theory]
    [InlineData("Ada\u001b[2JLovelace")]
    [InlineData("Ada\u001b]0;title\u0007")]
    [InlineData("Ada\rLovelace")]
    [InlineData("Ada\nLovelace")]
    [InlineData("Ada\tLovelace")]
    [InlineData("Ada\bLovelace")]
    [InlineData("Ada\u0000Lovelace")]
    [InlineData("Ada\u007fLovelace")]
    [InlineData("Ada\u009bLovelace")]
    public void CustomerName_RejectsEveryControlCharacter(string name)
    {
        ValidationException ex = Assert.Throws<ValidationException>(
            () => GuestText.RejectControlCharacters(name, specialRequests: null));

        Assert.Equal(ErrorCodes.BookingNameInvalid, ex.Code);
    }

    [Theory]
    [InlineData("José Núñez")]
    [InlineData("田中 太郎")]
    [InlineData("Ada 🐻 O'Brien-Smith")]
    [InlineData("")]
    [InlineData(null)]
    public void CustomerName_AcceptsOrdinaryText(string? name)
    {
        GuestText.RejectControlCharacters(name, specialRequests: null);
    }

    [Theory]
    [InlineData("nut allergy\nwindow table\r\n\tplease")]
    [InlineData("¿Mesa junto a la ventana? 🎂")]
    [InlineData(null)]
    public void SpecialRequests_AllowsLineBreaksAndTabsOnly(string? requests)
    {
        GuestText.RejectControlCharacters(customerName: null, requests);
    }

    [Theory]
    [InlineData("hi\u001b[2J")]
    [InlineData("hi\u001b]0;t\u0007")]
    [InlineData("hi\bthere")]
    [InlineData("hi\u0000there")]
    [InlineData("hi\u007fthere")]
    [InlineData("hi\u0085there")]
    [InlineData("hi\u009bthere")]
    public void SpecialRequests_RejectsAnyOtherControlCharacter(string requests)
    {
        ValidationException ex = Assert.Throws<ValidationException>(
            () => GuestText.RejectControlCharacters(customerName: null, requests));

        Assert.Equal(ErrorCodes.BookingSpecialRequestsInvalid, ex.Code);
    }
}
