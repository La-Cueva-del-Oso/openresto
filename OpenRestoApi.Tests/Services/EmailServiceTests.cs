using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Moq;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Email;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.Services;

public class EmailServiceTests
{
    [Fact]
    public async Task TestConnectionAsync_ReturnsFalse_WhenNoSettings()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(TestConnectionAsync_ReturnsFalse_WhenNoSettings));
        var protector = new Mock<ICredentialProtector>();
        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        var result = await service.TestConnectionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task TestConnectionAsync_ConnectsAndAuthenticates_WhenSettingsExist_Port587()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(TestConnectionAsync_ConnectsAndAuthenticates_WhenSettingsExist_Port587));
        db.Set<EmailSettings>().Add(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user",
            EncryptedPassword = "encrypted"
        });
        db.SaveChanges();

        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Decrypt("encrypted")).Returns("decrypted");

        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        var result = await service.TestConnectionAsync();

        Assert.True(result);
        clientMock.Verify(c => c.ConnectAsync("smtp.test.com", 587, SecureSocketOptions.StartTls, default), Times.Once);
        clientMock.Verify(c => c.AuthenticateAsync("user", "decrypted", default), Times.Once);
        clientMock.Verify(c => c.DisconnectAsync(true, default), Times.Once);
    }

    [Fact]
    public async Task TestConnectionAsync_Connects_WhenSettingsExist_Port25_NoSsl()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(TestConnectionAsync_Connects_WhenSettingsExist_Port25_NoSsl));
        db.Set<EmailSettings>().Add(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 25,
            Username = "user",
            EncryptedPassword = "encrypted",
            EnableSsl = false
        });
        db.SaveChanges();

        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Decrypt("encrypted")).Returns("decrypted");

        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        var result = await service.TestConnectionAsync();

        Assert.True(result);
        clientMock.Verify(c => c.ConnectAsync("smtp.test.com", 25, SecureSocketOptions.None, default), Times.Once);
    }

    [Fact]
    public async Task SendEmailAsync_Throws_WhenNoSettings()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(SendEmailAsync_Throws_WhenNoSettings));
        var protector = new Mock<ICredentialProtector>();
        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        await Assert.ThrowsAsync<InfrastructureException>(() =>
            service.SendEmailAsync("to@test.com", "Subject", "Body"));
    }

    [Fact]
    public async Task SendEmailAsync_SendsMessage_WhenSettingsExist()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(SendEmailAsync_SendsMessage_WhenSettingsExist));
        db.Set<EmailSettings>().Add(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 465,
            Username = "user",
            EncryptedPassword = "encrypted",
            EnableSsl = true,
            FromName = "Sender",
            FromEmail = "from@test.com"
        });
        db.SaveChanges();

        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Decrypt("encrypted")).Returns("decrypted");

        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        await service.SendEmailAsync("to@test.com", "Subject", "<h1>Body</h1>");

        clientMock.Verify(c => c.ConnectAsync("smtp.test.com", 465, SecureSocketOptions.SslOnConnect, default), Times.Once);
        clientMock.Verify(c => c.AuthenticateAsync("user", "decrypted", default), Times.Once);
        clientMock.Verify(c => c.SendAsync(It.IsAny<MimeMessage>(), default, It.IsAny<ITransferProgress>()), Times.Once);
        clientMock.Verify(c => c.DisconnectAsync(true, default), Times.Once);
    }

    [Fact]
    public async Task SendEmailAsync_UsesUsername_WhenFromEmailMissing()
    {
        using AppDbContext db = TestDbFactory.Create(nameof(SendEmailAsync_UsesUsername_WhenFromEmailMissing));
        db.Set<EmailSettings>().Add(new EmailSettings
        {
            Host = "smtp.test.com",
            Port = 587,
            Username = "user@test.com",
            EncryptedPassword = "encrypted",
            FromName = null,
            FromEmail = null
        });
        db.SaveChanges();

        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Decrypt("encrypted")).Returns("decrypted");

        var clientMock = new Mock<ISmtpClient>();
        var service = new EmailService(db, protector.Object, () => clientMock.Object);

        await service.SendEmailAsync("to@test.com", "Subject", "Body");

        clientMock.Verify(c => c.SendAsync(It.Is<MimeMessage>(m =>
            m.From.Count == 1 &&
            ((MailboxAddress)m.From[0]).Name == "OpenResto" &&
            ((MailboxAddress)m.From[0]).Address == "user@test.com"),
            It.IsAny<System.Threading.CancellationToken>(),
            It.IsAny<ITransferProgress>()), Times.Once);
    }

    private const string Sensitive = "535 5.7.8 user bob@corp.example rejected by smtp.internal.example";

    private static EmailService ServiceWithClient(string dbName, Mock<ISmtpClient> client)
    {
        AppDbContext db = TestDbFactory.Create(dbName);
        db.Set<EmailSettings>().Add(new EmailSettings
        {
            Host = "smtp.internal.example",
            Port = 587,
            Username = "bob@corp.example",
            EncryptedPassword = "encrypted",
        });
        db.SaveChanges();
        var protector = new Mock<ICredentialProtector>();
        protector.Setup(p => p.Decrypt("encrypted")).Returns("decrypted");
        return new EmailService(db, protector.Object, () => client.Object);
    }

    /// <summary>
    /// Whatever the SMTP stack throws becomes a category with a fixed message; its text rides only
    /// as the inner exception, which the log keeps and no response or table does.
    /// </summary>
    /// <seealso>EmailServiceTests.SendEmailAsync_SendsMessage_WhenSettingsExist</seealso>
    [Theory]
    [MemberData(nameof(SmtpFailures))]
    public async Task SendEmailAsync_WrapsAnSmtpFailure_InAFixedCategory(Exception thrown, string expectedCode)
    {
        var client = new Mock<ISmtpClient>();
        client.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SecureSocketOptions>(), default))
            .ThrowsAsync(thrown);
        EmailService service = ServiceWithClient(nameof(SendEmailAsync_WrapsAnSmtpFailure_InAFixedCategory) + expectedCode, client);

        InfrastructureException ex = await Assert.ThrowsAsync<InfrastructureException>(
            () => service.SendEmailAsync("to@test.com", "S", "B"));

        Assert.Equal(expectedCode, ex.Code);
        Assert.DoesNotContain("smtp.internal.example", ex.Message);
        Assert.DoesNotContain("bob@corp.example", ex.Message);
        Assert.Same(thrown, ex.InnerException);
    }

    public static TheoryData<Exception, string> SmtpFailures() => new()
    {
        { new AuthenticationException(Sensitive), ErrorCodes.EmailAuthFailed },
        { new System.Net.Sockets.SocketException(), ErrorCodes.EmailConnectionFailed },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, Sensitive), ErrorCodes.EmailRecipientRejected },
        { new InvalidOperationException(Sensitive), ErrorCodes.EmailSendFailed },
    };

    [Fact]
    public async Task SendEmailAsync_RejectsAnUnparseableRecipient_AsARecipientFailure()
    {
        EmailService service = ServiceWithClient(nameof(SendEmailAsync_RejectsAnUnparseableRecipient_AsARecipientFailure), new Mock<ISmtpClient>());

        InfrastructureException ex = await Assert.ThrowsAsync<InfrastructureException>(
            () => service.SendEmailAsync("not an address", "S", "B"));

        Assert.Equal(ErrorCodes.EmailRecipientRejected, ex.Code);
    }

    [Fact]
    public async Task TestConnectionAsync_WrapsAnAuthenticationFailure_WithoutItsText()
    {
        var client = new Mock<ISmtpClient>();
        client.Setup(c => c.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), default))
            .ThrowsAsync(new AuthenticationException(Sensitive));
        EmailService service = ServiceWithClient(nameof(TestConnectionAsync_WrapsAnAuthenticationFailure_WithoutItsText), client);

        InfrastructureException ex = await Assert.ThrowsAsync<InfrastructureException>(service.TestConnectionAsync);

        Assert.Equal(ErrorCodes.EmailAuthFailed, ex.Code);
        Assert.DoesNotContain("corp.example", ex.Message);
    }
}
