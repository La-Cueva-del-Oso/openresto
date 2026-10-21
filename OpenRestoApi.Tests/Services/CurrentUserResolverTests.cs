using Moq;
using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Application.Services;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Tests.Services;

public class CurrentUserResolverTests
{
    private static Mock<IAdminCredentialRepository> RepoWith(AdminCredential account)
    {
        var repo = new Mock<IAdminCredentialRepository>();
        repo.Setup(r => r.GetByIdAsync(account.Id)).ReturnsAsync(account);
        repo.Setup(r => r.GetByEmailAsync(account.Email)).ReturnsAsync(account);
        return repo;
    }

    [Fact]
    public async Task ResolveAsync_ResolvesTheActiveAccountTheIdNames()
    {
        var account = new AdminCredential { Id = 7, Email = "a@example.com", IsActive = true };

        Assert.Same(account, await CurrentUserResolver.ResolveAsync(7, RepoWith(account).Object));
    }

    [Fact]
    public async Task ResolveAsync_ResolvesNothing_ForADeactivatedAccount()
    {
        var account = new AdminCredential { Id = 7, Email = "a@example.com", IsActive = false };

        Assert.Null(await CurrentUserResolver.ResolveAsync(7, RepoWith(account).Object));
    }

    [Fact]
    public async Task ResolveAsync_ResolvesNothing_ForAnIdNoAccountHas()
    {
        var account = new AdminCredential { Id = 7, Email = "a@example.com", IsActive = true };

        Assert.Null(await CurrentUserResolver.ResolveAsync(8, RepoWith(account).Object));
    }

    [Fact]
    public async Task ResolveAsync_ResolvesNothing_ForATokenWithoutAUserId()
    {
        var account = new AdminCredential { Id = 7, Email = "a@example.com", IsActive = true };
        Mock<IAdminCredentialRepository> repo = RepoWith(account);

        Assert.Null(await CurrentUserResolver.ResolveAsync(
            new TestInfrastructure.FakeCurrentUser { Email = account.Email }, repo.Object));
        repo.Verify(r => r.GetByEmailAsync(It.IsAny<string>()), Times.Never);
    }
}
