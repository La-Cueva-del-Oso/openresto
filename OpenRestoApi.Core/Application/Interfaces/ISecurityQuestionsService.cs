using OpenRestoApi.Core.Application.DTOs;
using OpenRestoApi.Core.Application.Services;

namespace OpenRestoApi.Core.Application.Interfaces;

/// <summary>
/// Personal Verification Question (PVQ) lifecycle — the "forgot password" reset flow.
/// Answer hashing reuses <see cref="IPasswordService"/> after trim/lower normalisation.
/// Every question is per-account: the public lookup is keyed by email (the forgot-password
/// screen has no session yet), setup targets the signed-in caller.
/// </summary>
public interface ISecurityQuestionsService
{
    /// <summary>The question configured for <paramref name="email"/>, for the forgot-password screen.</summary>
    Task<PvqStatusDto> GetStatusAsync(string email);

    /// <summary>The signed-in caller's own question, for the settings screen.</summary>
    Task<PvqStatusDto> GetStatusForCurrentUserAsync();

    /// <summary>
    /// Sets the caller's question. False, with nothing changed, when <paramref name="currentPassword"/>
    /// is not the caller's: a stolen session must not be able to plant an answer it knows.
    /// </summary>
    Task<bool> SetupAsync(string currentPassword, string question, string answer);
    Task<PvqVerifyOutcome> VerifyAsync(string email, string answer);
}
