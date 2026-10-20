using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Utilities;

namespace OpenRestoApi.Tests.Utilities;

public class EmailFailureReasonTests
{
    [Fact]
    public void CodeFor_KeepsAKnownCode_AndFlattensEverythingElse()
    {
        var classified = new InfrastructureException("x") { Code = ErrorCodes.EmailRecipientRejected };
        var otherCode = new InfrastructureException("x") { Code = ErrorCodes.MediaHeroTooLarge };

        Assert.Equal(ErrorCodes.EmailRecipientRejected, EmailFailureReason.CodeFor(classified));
        Assert.Equal(ErrorCodes.EmailSendFailed, EmailFailureReason.CodeFor(otherCode));
        Assert.Equal(ErrorCodes.EmailSendFailed, EmailFailureReason.CodeFor(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Describe_FlattensAStoredValueThatIsNotACode()
    {
        (string code, string message) = EmailFailureReason.Describe("535 rejected by smtp.internal.example");

        Assert.Equal(ErrorCodes.EmailSendFailed, code);
        Assert.DoesNotContain("smtp.internal.example", message);
        Assert.Equal(ErrorCodes.EmailAuthFailed, EmailFailureReason.Describe(ErrorCodes.EmailAuthFailed).Code);
        Assert.Equal(ErrorCodes.EmailSendFailed, EmailFailureReason.Describe(null).Code);
    }
}
