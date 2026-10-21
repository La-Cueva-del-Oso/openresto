using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;
using OpenRestoApi.Infrastructure.Persistence;

namespace OpenRestoApi.Tests.Integration;

/// <summary>
/// A key minted by whoever held a compromised account is refused with a 401 once the account is
/// recovered, through the real <c>X-API-Key</c> pipeline. Signing out is not recovery, so a key
/// outlives it.
/// </summary>
public class ApiKeyRecoveryRevocationTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private const string OriginalPassword = "seeded-password";
    private const string NewPassword = "NewPassword123!";

    private readonly TestWebAppFactory _factory = factory;

    private async Task<AdminCredential> SeedOwnerAsync(string email)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (string hash, string salt) = scope.ServiceProvider.GetRequiredService<IPasswordService>().Hash(OriginalPassword);
        var user = new AdminCredential { Email = email, PasswordHash = hash, PasswordSalt = salt, Role = UserRoles.Owner };
        db.AdminCredentials.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private HttpClient SessionOf(AdminCredential user)
        => _factory.CreateClientWithToken(TestWebAppFactory.GenerateJwt(user.Id, user.Email, user.Role));

    private async Task<HttpClient> KeyClientAsync(AdminCredential owner)
    {
        HttpResponseMessage created = await SessionOf(owner).PostAsJsonAsync("/api/admin/api-keys", new
        {
            name = "Minted by the account",
            scopes = new[] { new { resource = "bookings", access = "read" } },
        });
        created.EnsureSuccessStatusCode();
        JsonElement json = await created.Content.ReadFromJsonAsync<JsonElement>();
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", json.GetProperty("secret").GetString()!);
        return client;
    }

    private static async Task<HttpStatusCode> OverviewStatusAsync(HttpClient keyClient)
        => (await keyClient.GetAsync("/api/admin/overview")).StatusCode;

    private async Task<(HttpClient VictimKey, HttpClient BystanderKey, AdminCredential Victim)> ArrangeAsync(string name)
    {
        AdminCredential victim = await SeedOwnerAsync($"{name}-victim@test.com");
        AdminCredential bystander = await SeedOwnerAsync($"{name}-bystander@test.com");
        HttpClient victimKey = await KeyClientAsync(victim);
        HttpClient bystanderKey = await KeyClientAsync(bystander);
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(victimKey));
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(bystanderKey));
        return (victimKey, bystanderKey, victim);
    }

    [Fact]
    public async Task APasswordChange_RefusesTheAccountsKey_AndNotAnotherAccounts()
    {
        var (victimKey, bystanderKey, victim) = await ArrangeAsync("change");

        HttpResponseMessage change = await SessionOf(victim).PostAsJsonAsync("/api/admin/auth/change-password",
            new { currentPassword = OriginalPassword, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await OverviewStatusAsync(victimKey));
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(bystanderKey));
    }

    [Fact]
    public async Task AFailedPasswordChange_LeavesTheAccountsKeyWorking()
    {
        var (victimKey, _, victim) = await ArrangeAsync("wrongpw");

        HttpResponseMessage change = await SessionOf(victim).PostAsJsonAsync("/api/admin/auth/change-password",
            new { currentPassword = "not-the-password", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(victimKey));
    }

    [Fact]
    public async Task AnOwnerPasswordReset_RefusesTheAccountsKey_AndNotAnotherAccounts()
    {
        var (victimKey, bystanderKey, victim) = await ArrangeAsync("ownerreset");

        HttpResponseMessage reset = await _factory.CreateAuthenticatedClient().PostAsJsonAsync(
            $"/api/admin/users/{victim.Id}/reset-password", new { newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await OverviewStatusAsync(victimKey));
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(bystanderKey));
    }

    [Fact]
    public async Task AResetByToken_RefusesTheAccountsKey_AndNotAnotherAccounts()
    {
        var (victimKey, bystanderKey, victim) = await ArrangeAsync("tokenreset");
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.AdminCredentials.Where(c => c.Id == victim.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ResetToken, "recovery-token")
                .SetProperty(c => c.ResetTokenExpiry, DateTime.UtcNow.AddMinutes(15)));
        }

        HttpResponseMessage reset = await _factory.CreateClient().PostAsJsonAsync("/api/admin/auth/reset-password",
            new { resetToken = "recovery-token", newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await OverviewStatusAsync(victimKey));
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(bystanderKey));
    }

    [Fact]
    public async Task ADeactivation_RefusesTheAccountsKey_AndReactivatingDoesNotBringItBack()
    {
        var (victimKey, bystanderKey, victim) = await ArrangeAsync("deactivate");
        HttpClient owner = _factory.CreateAuthenticatedClient();

        HttpResponseMessage deactivated = await owner.PatchAsJsonAsync($"/api/admin/users/{victim.Id}/active", new { isActive = false });
        HttpResponseMessage reactivated = await owner.PatchAsJsonAsync($"/api/admin/users/{victim.Id}/active", new { isActive = true });

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await OverviewStatusAsync(victimKey));
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(bystanderKey));
    }

    [Fact]
    public async Task SigningOut_LeavesTheAccountsKeyWorking()
    {
        var (victimKey, _, victim) = await ArrangeAsync("logout");

        HttpResponseMessage logout = await SessionOf(victim).PostAsync("/api/admin/auth/logout", null);

        Assert.True(logout.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, await OverviewStatusAsync(victimKey));
    }
}
