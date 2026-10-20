using OpenRestoApi.Core.Application.Exceptions;

namespace OpenRestoApi.Core.Application.Utilities;

/// <summary>
/// Why an email did not go out, as a stable code plus one fixed English sentence. What the SMTP
/// stack said — a host, a user name, a protocol reply — never reaches a client or the database;
/// it goes to the server log, and only this category is kept.
/// <seealso>EmailFailureReasonTests.CodeFor_KeepsAKnownCode_AndFlattensEverythingElse</seealso>
/// <seealso>EmailFailureReasonTests.Describe_FlattensAStoredValueThatIsNotACode</seealso>
/// </summary>
public static class EmailFailureReason
{
    private static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        [ErrorCodes.EmailNotConfigured] = "Email is not configured.",
        [ErrorCodes.EmailConnectionFailed] = "Could not connect to the mail server.",
        [ErrorCodes.EmailAuthFailed] = "The mail server rejected the username or password.",
        [ErrorCodes.EmailRecipientRejected] = "The mail server did not accept the recipient address.",
        [ErrorCodes.EmailSendFailed] = "The email could not be sent.",
    };

    public static string CodeFor(Exception ex)
        => ex is OpenRestoException { Code: { } code } && Messages.ContainsKey(code) ? code : ErrorCodes.EmailSendFailed;

    /// <summary>
    /// Resolves a stored reason. Rows written before reasons were codes hold raw exception text;
    /// anything that is not a known code comes back as the generic failure.
    /// </summary>
    public static (string Code, string Message) Describe(string? stored)
    {
        string code = stored is not null && Messages.ContainsKey(stored) ? stored : ErrorCodes.EmailSendFailed;
        return (code, Messages[code]);
    }
}
