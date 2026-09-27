using System.Security.Claims;

namespace MailVerifier.Web.Security;

public static class UserAccess
{
    public const string AdminPolicy = "Admin";

    private static readonly string[] RoleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    public static string? GetUserId(ClaimsPrincipal user)
    {
        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? user.FindFirst("sub")?.Value
            ?? user.Identity?.Name;
    }

    public static string? GetUserDisplayName(ClaimsPrincipal user)
    {
        return user.FindFirst("name")?.Value
            ?? user.FindFirst(ClaimTypes.Name)?.Value
            ?? user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst("email")?.Value
            ?? user.Identity?.Name
            ?? GetUserId(user);
    }

    public static bool IsAdmin(ClaimsPrincipal user) =>
        StandardRoles(user)
            .Concat(KeycloakRoles.FromClaims(user.Claims))
            .Any(role => string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));

    /// <summary>Roles from plain role claims; values may be comma, semicolon or space separated.</summary>
    public static IEnumerable<string> StandardRoles(ClaimsPrincipal user) =>
        user.Claims
            .Where(c => RoleClaimTypes.Contains(c.Type))
            .SelectMany(c => c.Value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
