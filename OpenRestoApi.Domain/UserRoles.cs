namespace OpenRestoApi.Core.Domain;

/// <summary>
/// The single place admin role values are defined. Adding a role (e.g. <c>Host</c>) means
/// adding one constant here plus one entry in <see cref="Assignable"/> — never a literal
/// sprinkled across controllers or services. Gating is done through the named policies in
/// <c>AuthPolicies</c>, so a new role does not require touching any <c>[Authorize]</c>
/// attribute.
/// </summary>
public static class UserRoles
{
    /// <summary>Full access, including user management.</summary>
    public const string Owner = "Owner";

    /// <summary>Full access to bookings/locations/brand, but cannot manage users.</summary>
    public const string Manager = "Manager";

    /// <summary>Roles that may be stored on a user row / assigned through the API.</summary>
    public static readonly HashSet<string> Assignable =
        new(StringComparer.OrdinalIgnoreCase) { Owner, Manager };

    /// <summary>
    /// Returns the canonically-cased role when <paramref name="role"/> is assignable, else null.
    /// Callers turn the null into a <c>ValidationException</c> with their own message.
    /// </summary>
    public static string? Normalise(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        string trimmed = role.Trim();
        return Assignable.Contains(trimmed)
            ? Assignable.First(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase))
            : null;
    }
}
