using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Interfaces;

/// <summary>Persistence of the singleton <see cref="EmailSettings"/> row (SMTP config).</summary>
public interface IEmailSettingsRepository
{
    /// <summary>The single settings row, or null if none exists yet.</summary>
    Task<EmailSettings?> GetAsync();

    /// <summary>Tracks a new settings row for insertion and saves, returning the persisted entity.</summary>
    Task<EmailSettings> AddAsync(EmailSettings settings);

    /// <summary>
    /// Writes every column of <paramref name="settings"/>, not only the ones changed since it was
    /// read. A save that wrote only its own changes could land beside another admin's concurrent
    /// one and pair that save's server with this save's password; a whole-row write keeps the
    /// destination and the credential of one request together.
    /// </summary>
    /// <seealso>EmailSettingsConcurrencyTests.A_Password_Saved_For_The_Real_Server_Never_Ends_Up_Beside_A_Concurrently_Saved_Server</seealso>
    /// <seealso>EmailSettingsConcurrencyTests.A_Save_With_The_Destination_Unchanged_And_No_Password_Keeps_The_Stored_One</seealso>
    Task UpdateAsync(EmailSettings settings);
}
