using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Infrastructure.Email;

public class EmailService(AppDbContext db, ICredentialProtector protector, Func<ISmtpClient> clientFactory) : IEmailService
{
    private readonly AppDbContext _db = db;
    private readonly ICredentialProtector _protector = protector;
    private readonly Func<ISmtpClient> _clientFactory = clientFactory;

    private async Task<EmailSettings?> GetSettingsAsync()
    {
        return await _db.Set<EmailSettings>().FirstOrDefaultAsync();
    }

    public async Task<bool> TestConnectionAsync()
    {
        EmailSettings? settings = await GetSettingsAsync();
        if (settings == null)
        {
            return false;
        }

        try
        {
            using ISmtpClient client = _clientFactory();
            SecureSocketOptions options = settings.Port == 587
                ? SecureSocketOptions.StartTls
                : settings.EnableSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;

            await client.ConnectAsync(settings.Host, settings.Port, options);
            await client.AuthenticateAsync(settings.Username, _protector.Decrypt(settings.EncryptedPassword));
            await client.DisconnectAsync(true);
            return true;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OpenRestoException))
        {
            throw Classify(ex);
        }
    }

    public async Task SendEmailAsync(string recipient, string subject, string htmlBody)
    {
        EmailSettings settings = await GetSettingsAsync()
            ?? throw new InfrastructureException("Email is not configured.") { Code = ErrorCodes.EmailNotConfigured };

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                settings.FromName ?? "OpenResto",
                settings.FromEmail ?? settings.Username));
            message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = htmlBody };

            using ISmtpClient client = _clientFactory();
            SecureSocketOptions options = settings.Port == 587
                ? SecureSocketOptions.StartTls
                : settings.EnableSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None;

            await client.ConnectAsync(settings.Host, settings.Port, options);
            await client.AuthenticateAsync(settings.Username, _protector.Decrypt(settings.EncryptedPassword));
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OpenRestoException))
        {
            throw Classify(ex);
        }
    }

    /// <summary>
    /// Turns whatever the SMTP stack threw into a category with a fixed message. The original rides
    /// along as the inner exception for the server log; its text must never be copied into the
    /// message, since it can name the host, the user name or the provider's internals.
    /// <seealso>EmailServiceTests.SendEmailAsync_WrapsAnAuthenticationFailure_WithoutItsText</seealso>
    /// </summary>
    private static InfrastructureException Classify(Exception ex)
    {
        string code = ex switch
        {
            AuthenticationException => ErrorCodes.EmailAuthFailed,
            ParseException or SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } => ErrorCodes.EmailRecipientRejected,
            SocketException or SslHandshakeException or ServiceNotConnectedException or ProtocolException
                or IOException or TimeoutException => ErrorCodes.EmailConnectionFailed,
            _ => ErrorCodes.EmailSendFailed,
        };
        return new InfrastructureException(EmailFailureReason.Describe(code).Message, ex) { Code = code };
    }
}
