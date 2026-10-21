using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Interfaces;

/// <summary>Persistence of <see cref="AdminApiKey"/> rows (issue #319 headless-CLI credentials).</summary>
public interface IAdminApiKeyRepository
{
    /// <summary>The key with the given id, or null. Used both by the management service (which
    /// then checks ownership) and by the authentication handler (which narrows straight to this
    /// row from the id segment of the raw key before hashing anything).</summary>
    Task<AdminApiKey?> GetByIdAsync(int id);

    /// <summary>All keys owned by the given user, newest first.</summary>
    Task<List<AdminApiKey>> GetByUserIdAsync(int userId);

    /// <summary>
    /// Adds and saves a new key (its id assigned), unless the owning account is no longer active
    /// at <paramref name="sessionVersion"/>, the version the minting session was authenticated
    /// with: a password change, reset or deactivation revokes the account's keys, so a mint that
    /// was in flight when one landed must not leave a live key behind. False means refused, and
    /// the row is gone again.
    /// </summary>
    /// <seealso>ApiKeyServiceTests.CreateAsync_IsRefusedAndLeavesNoKey_WhenTheSessionWasRevokedMeanwhile</seealso>
    /// <seealso>ApiKeyServiceTests.CreateAsync_IsRefusedAndLeavesNoKey_WhenTheAccountWasDeactivatedMeanwhile</seealso>
    /// <seealso>ApiKeyServiceTests.CreateAsync_MintsTheKey_WhenTheSessionIsStillTheAccountsCurrentOne</seealso>
    Task<bool> TryAddAsync(AdminApiKey key, int sessionVersion);

    /// <summary>Flushes pending changes (e.g. the hash/prefix set after the id is known, or a
    /// revoke/last-used update on a tracked entity).</summary>
    Task SaveChangesAsync();
}
