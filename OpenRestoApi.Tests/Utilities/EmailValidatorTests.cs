using OpenRestoApi.Core.Application.Utilities;

namespace OpenRestoApi.Tests.Utilities;

public class EmailValidatorTests
{
    [Theory]
    [InlineData("user@example.com")]
    [InlineData("john.doe@sub.example.co.uk")]
    [InlineData("a@b.co")]
    [InlineData("  trim-me@example.com  ")] // trimmed before matching
    [InlineData("Plus+Alias@example.com")]
    public void IsValid_AcceptsWellFormedEmails(string email)
    {
        Assert.True(EmailValidator.IsValid(email));
    }

    [Theory]
    [InlineData("josé+tag@example.cl")]
    [InlineData("o'neil@example.com")]
    [InlineData("ana@bücher.example")]
    public void IsValid_AcceptsOrdinaryAddresses(string email)
    {
        Assert.True(EmailValidator.IsValid(email));
    }

    [Theory]
    [InlineData("gu\u001best@example.com")]     // ESC
    [InlineData("guest@exa\u0000mple.com")]     // NUL
    [InlineData("guest\u007f@example.com")]     // DEL
    [InlineData("guest@example.c\u009fom")]     // C1
    [InlineData("guest@\u001b[2Jexample.com")]  // a terminal escape sequence
    public void IsValid_RejectsControlCharacters(string email)
    {
        Assert.False(EmailValidator.IsValid(email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.com")]
    [InlineData("no-tld@example")]
    [InlineData("spaces @example.com")]
    [InlineData("@nouser.com")]
    [InlineData("nouser@.com")]
    public void IsValid_RejectsMalformedEmails(string? email)
    {
        Assert.False(EmailValidator.IsValid(email));
    }
}
