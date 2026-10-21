using System.Text.RegularExpressions;

namespace OpenRestoApi.Core.Application.Utilities;

/// <summary>
/// Centralised email format validation. The same regex was previously inlined in
/// <c>AuthController.IsValidEmail</c> and duplicated on the frontend (<c>useTableHold.ts</c>);
/// the frontend mirrors this in <c>utils/validation.ts</c>. This is a deliberately loose shape
/// check, not RFC 5322 — strict validation happens via the confirmation email round-trip. It does
/// refuse control characters (\p{Cc}): an address is printed by the CLI and written into mail
/// headers, and no real one carries an ESC or a NUL.
/// </summary>
/// <seealso>EmailValidatorTests.IsValid_RejectsControlCharacters</seealso>
/// <seealso>EmailValidatorTests.IsValid_AcceptsOrdinaryAddresses</seealso>
public static class EmailValidator
{
    private static readonly Regex EmailPattern =
        new(@"^[^\s@\p{Cc}]+@[^\s@\p{Cc}]+\.[^\s@\p{Cc}]+$", RegexOptions.Compiled);

    public static bool IsValid(string? email)
        => !string.IsNullOrWhiteSpace(email) && EmailPattern.IsMatch(email.Trim());
}
