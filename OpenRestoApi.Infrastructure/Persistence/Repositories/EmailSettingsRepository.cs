using CustomAccessibility.Attributes;
using Microsoft.EntityFrameworkCore;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Infrastructure.Persistence.Repositories;

[OnlyAccessibleBy("OpenRestoApi.Extensions.ServiceCollectionExtensions")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.EmailSettingsServiceTests")]
[OnlyAccessibleBy("OpenRestoApi.Tests.Services.EmailSettingsConcurrencyTests")]
[ExternalAccessAllowed]
internal class EmailSettingsRepository(AppDbContext db) : IEmailSettingsRepository
{
    private readonly AppDbContext _db = db;

    public async Task<EmailSettings?> GetAsync()
    {
        return await _db.Set<EmailSettings>().FirstOrDefaultAsync();
    }

    public async Task<EmailSettings> AddAsync(EmailSettings settings)
    {
        _db.Set<EmailSettings>().Add(settings);
        await _db.SaveChangesAsync();
        return settings;
    }

    public async Task UpdateAsync(EmailSettings settings)
    {
        _db.Entry(settings).State = EntityState.Modified;
        await _db.SaveChangesAsync();
    }
}
