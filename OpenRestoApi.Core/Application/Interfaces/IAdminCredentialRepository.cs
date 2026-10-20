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
    /// Deactivates <paramref name="user"/> and ends its sessions, under the same one-statement
    /// last-Owner rule as <see cref="TrySetRoleAsync"/>.
    /// </summary>
    /// <seealso>AdminCredentialConcurrencyTests.Two_Owners_Deactivating_Each_Other_Concurrently_Leave_One_Active_Owner</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Deactivations_Succeed_Concurrently_While_Another_Active_Owner_Remains</seealso>
    /// <seealso>AdminCredentialConcurrencyTests.Three_Owners_Deactivated_Concurrently_Keep_Exactly_One</seealso>
    Task<bool> TryDeactivateAsync(AdminCredential user);

    /// <summary>Adds and saves a new credential, returning the persisted entity.</summary>
    Task<AdminCredential> AddAsync(AdminCredential credential);

    /// <summary>Flushes pending changes (e.g. mutated hash/token fields on a tracked entity).</summary>
    Task SaveChangesAsync();
}
