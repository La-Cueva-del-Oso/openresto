using OpenRestoApi.Core.Application.Interfaces;
using OpenRestoApi.Core.Domain;

namespace OpenRestoApi.Core.Application.Services;

/// <summary>
/// Turns the claims on the request into the account row they name. Shared by every service
/// that acts "as the caller" (<see cref="AuthService"/>, <see cref="SecurityQuestionsService"/>,
/// <see cref="UserService"/>) so they all apply the same two rules: the id claim is the stable
/// identity, and a deactivated account resolves to nothing.
/// </summary>
public static class CurrentUserResolver
{
    /// <summary>The active account the caller's id claim names, or null.</summary>
    /// <seealso>CurrentUserResolverTests.ResolveAsync_ResolvesNothing_ForATokenWithoutAUserId</seealso>
    public static Task<AdminCredential?> ResolveAsync(
        ICurrentUserService currentUser,
        IAdminCredentialRepository credentialRepository)
        => ResolveAsync(currentUser.UserId, credentialRepository);

    /// <inheritdoc cref="ResolveAsync(ICurrentUserService, IAdminCredentialRepository)"/>
    public static async Task<AdminCredential?> ResolveAsync(
        int? userId,
        IAdminCredentialRepository credentialRepository)
    {
        if (!userId.HasValue) return null;

        AdminCredential? cred = await credentialRepository.GetByIdAsync(userId.Value);
        return cred?.IsActive == true ? cred : null;
    }
}
