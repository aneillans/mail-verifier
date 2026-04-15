using System.Security.Claims;
using System.Text.Json;

namespace MailVerifier.Web.Security;

public static class UserAccess
{
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

    public static bool IsAdmin(ClaimsPrincipal user)
    {
        if (user.IsInRole("Admin") || user.IsInRole("admin"))
            return true;

        foreach (var claim in user.Claims)
        {
            if (claim.Type != ClaimTypes.Role && claim.Type != "role" && claim.Type != "roles")
                continue;

            var roleValues = claim.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (roleValues.Any(v => string.Equals(v, "admin", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        if (HasAdminInKeycloakClaim(user, "realm_access"))
            return true;

        if (HasAdminInKeycloakClaim(user, "resource_access"))
            return true;

        return false;
    }

    private static bool HasAdminInKeycloakClaim(ClaimsPrincipal user, string claimType)
    {
        foreach (var claim in user.FindAll(claimType))
        {
            if (!TryParseJsonObject(claim.Value, out var root))
                continue;

            if (string.Equals(claimType, "realm_access", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsAdminRole(root))
                    return true;
            }
            else
            {
                foreach (var client in root.EnumerateObject())
                {
                    if (ContainsAdminRole(client.Value))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsAdminRole(JsonElement element)
    {
        if (!element.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind != JsonValueKind.String)
                continue;

            var value = role.GetString();
            if (string.Equals(value, "admin", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryParseJsonObject(string value, out JsonElement root)
    {
        root = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}