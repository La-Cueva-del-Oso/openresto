using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Services;

public class EmailSettingsService(
    IEmailSettingsRepository settingsRepository,
    IEmailFailureRepository emailFailureRepository,
    ICredentialProtector protector,
    IEmailService emailService,
    IAuditScope? audit = null)
{
    /// <summary>
    /// Castle's generated proxy constructors drop default values, so a Moq class mock reaches only
    /// a constructor of exactly matching arity. This is that constructor.
    /// </summary>
    public EmailSettingsService(
        IEmailSettingsRepository settings,
        IEmailFailureRepository failures,
        ICredentialProtector credentialProtector,
        IEmailService email)
        : this(settings, failures, credentialProtector, email, null) { }

    private const string PasswordMask = "••••••••";

    private readonly IAuditScope _audit = audit ?? NullAuditScope.Instance;
    private readonly IEmailSettingsRepository _settingsRepository = settingsRepository;
    private readonly IEmailFailureRepository _emailFailureRepository = emailFailureRepository;
    private readonly ICredentialProtector _protector = protector;
    private readonly IEmailService _emailService = emailService;

    public virtual async Task<EmailSettings?> GetAsync()
    {
        return await _settingsRepository.GetAsync();
    }

    /// <summary>
    /// The stored password stays usable only against the destination it was saved for: moving the
    /// host, port, username or encryption needs the password typed again.
    /// </summary>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_KeepsStoredPassword_WhenDestinationIsUnchanged</seealso>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_RejectsChangedDestination_WhenNoNewPassword</seealso>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_AcceptsChangedDestination_WhenNewPasswordIsTyped</seealso>
    public virtual async Task SaveAsync(
        string host, int port, string username, string? password,
        bool enableSsl, string? fromName, string? fromEmail,
        bool sendBookingConfirmations = false)
    {
        host = RequireUsableServer(host, port, username);
        EmailSettings? settings = await _settingsRepository.GetAsync();
        bool isNew = false;
        if (settings == null)
        {
            settings = new EmailSettings();
            isNew = true;
        }

        bool hasNewPassword = !string.IsNullOrEmpty(password) && password != PasswordMask;
        if (!hasNewPassword
            && !string.IsNullOrEmpty(settings.EncryptedPassword)
            && !SameDestination(settings, host, port, username, enableSsl))
        {
            throw new ValidationException(
                "Re-enter the email password to change the server, port, username or encryption.")
            { Code = ErrorCodes.EmailPasswordRequired };
        }

        // Named one by one rather than serialized from the request: the request also carries the
        // SMTP password, and an audit entry may never hold a credential.
        _audit.RecordChange("host", settings.Host, host);
        _audit.RecordChange("port", settings.Port, port);
        _audit.RecordChange("username", settings.Username, username);
        _audit.RecordChange("enableSsl", settings.EnableSsl, enableSsl);
        _audit.RecordChange("fromName", settings.FromName, fromName);
        _audit.RecordChange("fromEmail", settings.FromEmail, fromEmail);
        _audit.RecordChange("sendBookingConfirmations", settings.SendBookingConfirmations, sendBookingConfirmations);

        settings.Host = host;
        settings.Port = port;
        settings.Username = username;
        settings.EnableSsl = enableSsl;
        settings.FromName = fromName;
        settings.FromEmail = fromEmail;
        settings.SendBookingConfirmations = sendBookingConfirmations;

        if (hasNewPassword)
        {
            string? previous = settings.EncryptedPassword;
            settings.EncryptedPassword = _protector.Encrypt(password);
            // Both sides mask to the redaction marker, so the entry carries the fact of the
            // change and nothing more.
            _audit.RecordChange("password", previous, settings.EncryptedPassword);
        }

        if (isNew)
        {
            await _settingsRepository.AddAsync(settings);
        }
        else
        {
            await _settingsRepository.UpdateAsync(settings);
        }

        _audit.Describe(AuditActions.EmailSettingsUpdate, AuditTargets.EmailSettings,
            summary: $"Updated the outgoing email settings ({host}:{port})");
    }

    /// <summary>
    /// A host, port and username the mail library cannot connect and sign in with are refused
    /// rather than stored: the settings screen already requires the first and last, and a blank
    /// host would leave every guest email failing with a connection error. Returns the trimmed host.
    /// </summary>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_RejectsABlankHost</seealso>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_RejectsABlankUsername</seealso>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_RejectsAPortOutsideTheValidRange</seealso>
    /// <seealso>EmailSettingsServiceTests.SaveAsync_AcceptsThePortRangeLimits</seealso>
    private static string RequireUsableServer(string? host, int port, string? username)
    {
        string trimmedHost = (host ?? string.Empty).Trim();
        if (trimmedHost.Length == 0)
        {
            throw new ValidationException("Enter the address (host) of the mail server.") { Code = ErrorCodes.EmailHostRequired };
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ValidationException("Enter the username for the mail server.") { Code = ErrorCodes.EmailUsernameRequired };
        }

        if (port is < 1 or > 65535)
        {
            throw new ValidationException("The port must be a number between 1 and 65535.") { Code = ErrorCodes.EmailPortInvalid };
        }

        return trimmedHost;
    }

    private static bool SameDestination(EmailSettings stored, string host, int port, string username, bool enableSsl)
        => string.Equals(stored.Host?.Trim(), host?.Trim(), StringComparison.OrdinalIgnoreCase)
           && stored.Port == port
           && string.Equals(stored.Username, username, StringComparison.Ordinal)
           && stored.EnableSsl == enableSsl;

    public virtual async Task<bool> TestConnectionAsync()
    {
        bool connected = await _emailService.TestConnectionAsync();
        _audit.Describe(AuditActions.EmailSettingsTest, AuditTargets.EmailSettings,
            summary: connected
                ? "Tested the outgoing email connection — succeeded"
                : "Tested the outgoing email connection — failed");
        return connected;
    }

    public virtual async Task<IReadOnlyList<EmailFailure>> GetFailuresAsync()
    {
        return await _emailFailureRepository.GetRecentAsync(50);
    }
}
