using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Interfaces;

/// <summary>
/// Persistence of admin user accounts (<see cref="AdminCredential"/>): login email + password
/// hash + PVQ reset question + role. All email lookups are persisted case-insensitively via
/// SQLite <c>lower()</c>; pass the raw email and the repo normalises it.
/// </summary>
public interface IAdminCredentialRepository
{
    /// <summary>The credential with the given id, or null.</summary>
    Task<AdminCredential?> GetByIdAsync(int id);

    /// <summary>The credential matching the given email (case-insensitive), or null.</summary>
    Task<AdminCredential?> GetByEmailAsync(string email);

    /// <summary>The credential matching the given password-reset token, or null.</summary>
    Task<AdminCredential?> GetByResetTokenAsync(string resetToken);

    /// <summary>All credentials, oldest first.</summary>
    Task<List<AdminCredential>> GetAllAsync();

    /// <summary>
    /// Gives <paramref name="user"/> the role unless that would leave the instance without an
    /// active Owner; false means refused and nothing changed. The check and the write are one
    /// statement: counting first and writing after lets two Owners demoting each other both pass.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.Two_Owners_Demoting_Each_Other_Concurrently_Leave_One_Active_Owner</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Demotions_Succeed_Concurrently_While_Another_Active_Owner_Remains</seealso>
    Task<bool> TrySetRoleAsync(AdminCredential user, string role);

    /// <summary>
    /// Deactivates <paramref name="user"/>, ends its sessions and revokes its API keys, under the
    /// same one-statement last-Owner rule as <see cref="TrySetRoleAsync"/>.
    /// </summary>
    /// <seealso>AdminApiKeyRevocationTests.Deactivating_An_Account_Revokes_Its_Live_Api_Keys_And_Only_Those</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Two_Owners_Deactivating_Each_Other_Concurrently_Leave_One_Active_Owner</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Deactivations_Succeed_Concurrently_While_Another_Active_Owner_Remains</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Three_Owners_Deactivated_Concurrently_Keep_Exactly_One</seealso>
    Task<bool> TryDeactivateAsync(AdminCredential user);

    /// <summary>
    /// Counts one security-answer attempt against the account, locking it until
    /// <paramref name="lockedUntilUtc"/> (and zeroing the count) on the <paramref name="maxAttempts"/>th.
    /// False when the account is locked at <paramref name="nowUtc"/> and so takes no attempt. The
    /// lock test and the count are one statement, and it runs before the answer is checked:
    /// reading the count, checking and writing it back lets a burst of guesses all start from
    /// the same number.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.Concurrent_Wrong_Answers_Lock_The_Account_After_The_Limit</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Concurrent_Wrong_Answers_Below_The_Limit_Are_All_Counted</seealso>
    Task<bool> TryCountPvqAttemptAsync(int id, DateTime nowUtc, int maxAttempts, DateTime lockedUntilUtc);

    /// <summary>
    /// A right answer: clears the attempt count and any lock, and stores the reset token it earns.
    /// False, and nothing stored, unless the account is still active, at the
    /// <see cref="AdminCredential.SessionVersion"/> and with the answer <paramref name="verified"/>
    /// was read with: an Owner's reset landing while the answer was being checked must not be
    /// followed by a token minted from the answer it cleared.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.A_Security_Answer_Checked_Before_An_Owner_Reset_Earns_No_Reset_Token</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.A_Security_Answer_Checked_Before_The_Question_Changed_Earns_No_Reset_Token</seealso>
    /// <seealso>SecurityQuestionsServiceTests.VerifyAsync_Returns_Success_And_Mints_Reset_Token_On_Correct_Answer</seealso>
    Task<bool> TryCompletePvqVerificationAsync(AdminCredential verified, string resetToken, DateTime resetTokenExpiryUtc);

    /// <summary>
    /// Consumes <paramref name="resetToken"/>: sets the new password and revokes the account's
    /// sessions (which withdraws the token), API keys and push subscriptions in one transaction, held to the token
    /// still being the stored, unexpired one at <paramref name="nowUtc"/>, to the account being
    /// active and to the <see cref="AdminCredential.SessionVersion"/> <paramref name="credential"/>
    /// was read with. False means refused and nothing changed: a password change, an Owner's
    /// reset or another request spending the same token got there first.
    /// </summary>
    /// <seealso>AdminApiKeyRevocationTests.Resetting_The_Password_With_A_Token_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those</seealso>
    /// <seealso>AdminApiKeyRevocationTests.A_Refused_Token_Reset_Leaves_The_Accounts_Api_Keys_Alone</seealso>
    /// <seealso>AdminApiKeyRevocationTests.Resetting_With_A_Token_Is_Refused_For_A_Deactivated_Account</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.A_Reset_Link_Read_Before_The_Password_Changed_Cannot_Set_A_Password_Afterwards</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.A_Reset_Link_Is_Spent_Once_When_Two_Requests_Read_It</seealso>
    /// <seealso>AuthServiceTests.ResetPasswordAsync_Succeeds_Clears_Token_And_Allows_New_Password</seealso>
    /// <seealso>AuthServiceTests.ResetPasswordAsync_Returns_False_For_Expired_Token</seealso>
    Task<bool> TryResetPasswordAsync(AdminCredential credential, string resetToken, DateTime nowUtc, string passwordHash, string passwordSalt);

    /// <summary>
    /// Sets the new password on <paramref name="credential"/> and revokes its sessions, pending
    /// reset token, API keys and push subscriptions in one transaction, held to the account being
    /// active and to the <see cref="AdminCredential.SessionVersion"/> it was read with. False means
    /// refused and nothing changed: a reset, a deactivation, a sign-out or another change got there first.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.A_Recovery_Whose_Push_Subscription_Delete_Fails_Changes_Nothing</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.A_Password_Change_Read_Before_An_Owner_Reset_Is_Refused</seealso>
    /// <seealso>AdminApiKeyRevocationTests.Changing_The_Password_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those</seealso>
    /// <seealso>AdminApiKeyRevocationTests.A_Refused_Password_Change_Leaves_The_Accounts_Api_Keys_Alone</seealso>
    Task<bool> TryChangePasswordAsync(AdminCredential credential, string passwordHash, string passwordSalt);

    /// <summary>
    /// Sets the sign-in email of <paramref name="credential"/>, held to the account being active
    /// and to the <see cref="AdminCredential.SessionVersion"/> it was read with; the version is
    /// left alone, as the caller re-issues its own token. False means refused and nothing changed:
    /// the account's sessions were revoked since it was read, or another account took the address
    /// (the unique index decides that race, not the caller's earlier lookup).
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.An_Email_Change_Read_Before_An_Owner_Reset_Is_Refused</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Two_Accounts_Changing_To_The_Same_Email_Concurrently_Leave_One_Owner_Of_It</seealso>
    Task<bool> TryChangeEmailAsync(AdminCredential credential, string email);

    /// <summary>
    /// Stores the security question on <paramref name="credential"/> unless its sessions were
    /// revoked (a password change or reset, a deactivation, a sign-out) since it was read; false
    /// means refused and nothing changed.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.A_Security_Question_Set_Up_While_An_Owner_Resets_The_Password_Is_Not_Stored</seealso>
    /// <seealso>SecurityQuestionsServiceTests.SetupAsync_Saves_The_Question_When_The_Current_Password_Is_Right</seealso>
    Task<bool> TrySetSecurityQuestionAsync(AdminCredential credential, string question, string answerHash, string answerSalt);

    /// <summary>
    /// Signs the account out everywhere and withdraws any pending self-service reset token: a
    /// token issued before the credential changed (or the account was shut) must not outlive it.
    /// Saves whatever else is pending on <paramref name="credential"/> in the same transaction and
    /// leaves its <see cref="AdminCredential.SessionVersion"/> at the stored value. The version is
    /// raised in SQL: two revocations that each read it, add one and write it back leave it one
    /// short, and the second one would not count.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.Concurrent_Revocations_Are_All_Counted</seealso>
    /// <seealso>AdminCredentialRepositoryTests.RevokeSessionsAsync_Raises_The_Version_By_One_And_Drops_A_Pending_Reset_Token</seealso>
    /// <seealso>AdminCredentialRepositoryTests.RevokeSessionsAsync_Saves_The_Pending_Changes_With_The_Revocation</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.A_Revocation_Retried_After_A_Busy_Database_Still_Saves_The_Pending_Changes</seealso>
    Task RevokeSessionsAsync(AdminCredential credential);

    /// <summary>
    /// <see cref="RevokeSessionsAsync"/> for an account being recovered (an Owner's password
    /// reset): the same transaction also revokes the account's live API keys and deletes its push
    /// subscriptions, since a key or a browser registered by whoever had the account outlives its
    /// sessions otherwise. A plain sign-out is not recovery and uses <see cref="RevokeSessionsAsync"/>.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.A_Recovery_Whose_Push_Subscription_Delete_Fails_Changes_Nothing</seealso>
    /// <seealso>AdminApiKeyRevocationTests.An_Owner_Reset_Revokes_The_Accounts_Live_Api_Keys_And_Only_Those</seealso>
    /// <seealso>AdminApiKeyRevocationTests.Signing_Out_Leaves_The_Accounts_Api_Keys_Alone</seealso>
    Task RevokeSessionsAndApiKeysAsync(AdminCredential credential);

    /// <summary>Adds and saves a new credential, returning the persisted entity.</summary>
    Task<AdminCredential> AddAsync(AdminCredential credential);

    /// <summary>Flushes pending changes (e.g. mutated hash/token fields on a tracked entity).</summary>
    Task SaveChangesAsync();
}
