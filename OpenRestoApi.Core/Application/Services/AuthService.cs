using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Exceptions;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Mappings;
using OpenRestoApi.Core.Application.Utilities;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Services;

/// <summary>
/// Admin authentication orchestrator. Delegates password hashing to
/// <see cref="IPasswordService"/>, JWT minting to <see cref="IJwtTokenService"/>, and
/// PVQ/reset-question concerns to <see cref="ISecurityQuestionsService"/>.
/// Every self-service operation targets the caller resolved from
/// <see cref="ICurrentUserService"/> — never "the" credential row.
/// </summary>
public class AuthService(
    IAdminCredentialRepository credentialRepository,
    IPasswordService passwordService,
    IJwtTokenService jwtTokenService,
    ICurrentUserService currentUser,
    IAdminPushSubscriptionRepository pushSubscriptionRepository,
    IAuditScope? audit = null) : IAuthService
{
    private readonly IAdminPushSubscriptionRepository _pushSubscriptionRepository = pushSubscriptionRepository;
    private readonly IAdminCredentialRepository _credentialRepository = credentialRepository;
    private readonly IPasswordService _passwordService = passwordService;
    private readonly IJwtTokenService _jwtTokenService = jwtTokenService;
    private readonly ICurrentUserService _currentUser = currentUser;
    private readonly IAuditScope _audit = audit ?? NullAuditScope.Instance;

    public virtual async Task<string?> LoginAsync(string email, string password)
    {
        string attempted = email ?? string.Empty;
        AdminCredential? cred = await _credentialRepository.GetByEmailAsync(attempted);
        // Unknown email, deactivated account, and wrong password are deliberately
        // indistinguishable to the caller — the controller turns all three into the same 401.
        if (cred == null || !cred.IsActive)
            return RecordFailedLogin(attempted);
        if (!CredentialHelper.VerifyPassword(cred, password, _passwordService))
            return RecordFailedLogin(attempted);

        AttributeTo(cred);
        Describe(AuditActions.AuthLogin, cred, "Signed in");
        return Mint(cred);
    }

    /// <summary>
    /// The attempted address is the point of the entry, but the summary stays as uninformative as
    /// the 401 the caller gets: whether the address exists is not something a rejected attempt
    /// gets to learn from the audit trail either.
    /// </summary>
    private string? RecordFailedLogin(string attemptedEmail)
    {
        _audit.AttributeTo(null, attemptedEmail, role: string.Empty);
        _audit.Describe(AuditActions.AuthLoginFailed, summary: "Failed sign-in attempt");
        return null;
    }

    public virtual async Task<CurrentUserDto?> GetCurrentUserAsync()
    {
        AdminCredential? cred = await ResolveCurrentUserAsync();
        return cred == null ? null : UserMapper.ToCurrentUserDto(cred);
    }

    /// <summary>
    /// A changed password drops the account's push subscriptions along with its sessions and API
    /// keys: all are credentials a person who knew the old password may still hold. Null when the
    /// current password is wrong or the account changed under the request (a reset, a deactivation).
    /// </summary>
    /// <seealso>AuthServiceTests.ChangePasswordAsync_Invalidates_A_Pending_Reset_Token</seealso>
    /// <seealso>AuthServiceTests.ChangePasswordAsync_Keeps_The_Security_Question</seealso>
    /// <seealso>AuthServiceTests.ChangePasswordAsync_Deletes_The_Callers_Push_Subscriptions_Only</seealso>
    /// <seealso>AuthServiceTests.ChangePasswordAsync_Keeps_Push_Subscriptions_When_The_Current_Password_Is_Wrong</seealso>
    public virtual async Task<string?> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        UserFields.ValidatePassword(newPassword);
        AdminCredential? cred = await ResolveCurrentUserAsync();
        if (cred == null || !CredentialHelper.VerifyPassword(cred, currentPassword, _passwordService))
            return null;
        string previousHash = cred.PasswordHash;
        (string hash, string salt) = _passwordService.Hash(newPassword);
        // Refused when a reset, a deactivation or another change landed since the account was read.
        if (!await _credentialRepository.TryChangePasswordAsync(cred, hash, salt))
            return null;
        await _pushSubscriptionRepository.DeleteForUserAsync(cred.Id);

        _audit.RecordChange("passwordHash", previousHash, cred.PasswordHash);
        Describe(AuditActions.AuthPasswordChange, cred, $"Changed the password for {cred.Email}");
        // Every other session is now refused; this one carries on under a fresh token.
        return Mint(cred);
    }

    public virtual async Task<string?> ChangeEmailAsync(string currentPassword, string newEmail)
    {
        string normalizedEmail = UserFields.NormalizeEmail(newEmail);
        AdminCredential? cred = await ResolveCurrentUserAsync();
        if (cred == null || !CredentialHelper.VerifyPassword(cred, currentPassword, _passwordService))
            return null;
        if (string.Equals(normalizedEmail, cred.Email, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("New email must be different from the current email.") { Code = ErrorCodes.AuthEmailUnchanged };

        AdminCredential? existing = await _credentialRepository.GetByEmailAsync(normalizedEmail);
        if (existing != null && existing.Id != cred.Id)
            throw new BusinessRuleException("That email address is already in use.") { Code = ErrorCodes.AuthEmailAlreadyInUse };

        string previousEmail = cred.Email;
        if (!await _credentialRepository.TryChangeEmailAsync(cred, normalizedEmail))
        {
            // The lookup above is not a lock: another account may have taken the address since.
            existing = await _credentialRepository.GetByEmailAsync(normalizedEmail);
            if (existing != null && existing.Id != cred.Id)
                throw new BusinessRuleException("That email address is already in use.") { Code = ErrorCodes.AuthEmailAlreadyInUse };
            return null;
        }

        _audit.RecordChange("email", previousEmail, normalizedEmail);
        Describe(AuditActions.AuthEmailChange, cred,
            $"Changed the sign-in email from {previousEmail} to {normalizedEmail}");
        // Re-mint so the token's email claim matches the row it identifies.
        return Mint(cred);
    }

    /// <seealso>AuthServiceTests.ResetPasswordAsync_Rejects_A_Missing_Token_Without_Touching_Any_Account</seealso>
    /// <seealso>AuthServiceTests.ResetPasswordAsync_Deletes_The_Resetting_Accounts_Push_Subscriptions_Only</seealso>
    /// <seealso>AuthServiceTests.ResetPasswordAsync_Keeps_Push_Subscriptions_For_An_Unknown_Token</seealso>
    public virtual async Task<bool> ResetPasswordAsync(string resetToken, string newPassword)
    {
        UserFields.ValidatePassword(newPassword);
        // An absent token must never reach the lookup: a null would match every account with no
        // reset pending.
        if (string.IsNullOrEmpty(resetToken))
            return false;
        AdminCredential? cred = await _credentialRepository.GetByResetTokenAsync(resetToken);
        if (cred == null)
            return false;
        string previousHash = cred.PasswordHash;
        (string hash, string salt) = _passwordService.Hash(newPassword);
        if (!await _credentialRepository.TryResetPasswordAsync(cred, resetToken, DateTime.UtcNow, hash, salt))
            return false;
        await _pushSubscriptionRepository.DeleteForUserAsync(cred.Id);

        // The reset arrives with a token rather than a session, so the actor is whoever the token
        // resolved to — the request's claims would name nobody.
        AttributeTo(cred);
        _audit.RecordChange("passwordHash", previousHash, cred.PasswordHash);
        Describe(AuditActions.AuthPasswordReset, cred, $"Reset the password for {cred.Email}");
        return true;
    }

    /// <summary>
    /// Signs the caller out everywhere. Not account recovery, so the account's API keys stay valid.
    /// </summary>
    /// <seealso>AdminApiKeyRevocationTests.Signing_Out_Leaves_The_Accounts_Api_Keys_Alone</seealso>
    /// <seealso>AuthServiceTests.LogoutAsync_Keeps_The_Callers_Push_Subscriptions</seealso>
    public virtual async Task LogoutAsync()
    {
        if (_currentUser.IsApiKeyAuthenticated)
            return;
        AdminCredential? cred = await ResolveCurrentUserAsync();
        if (cred == null)
            return;
        await _credentialRepository.RevokeSessionsAsync(cred);
    }

    private string Mint(AdminCredential cred)
        => _jwtTokenService.Generate(cred.Id, cred.Email, cred.Role, cred.SessionVersion);

    private void AttributeTo(AdminCredential cred)
        => _audit.AttributeTo(cred.Id, cred.Email, cred.DisplayName, cred.Role);

    private void Describe(string action, AdminCredential cred, string summary)
        => _audit.Describe(action, AuditTargets.User, AuditTargets.IdOf(cred.Id),
            UserFields.PersonLabel(cred.DisplayName, cred.Email), summary: summary);

    private Task<AdminCredential?> ResolveCurrentUserAsync()
        => CurrentUserResolver.ResolveAsync(_currentUser, _credentialRepository);
}
