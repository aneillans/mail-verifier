using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace MailVerifier.Web.Security;

/// <summary>Reads Keycloak roles from realm_access / resource_access claims or raw JWT payloads.</summary>
public static class KeycloakRoles
{
    public const string RealmAccessClaim = "realm_access";
    public const string ResourceAccessClaim = "resource_access";

    /// <summary>
    /// Roles from realm_access and resource_access JSON claims. When <paramref name="clientId"/> is set, only
    /// that client's and the "account" client's resource roles are included; otherwise all clients are.
    /// </summary>
    public static IEnumerable<string> FromClaims(IEnumerable<Claim> claims, string? clientId = null)
    {
        foreach (var claim in claims)
        {
            var isRealm = string.Equals(claim.Type, RealmAccessClaim, StringComparison.OrdinalIgnoreCase);
            var isResource = string.Equals(claim.Type, ResourceAccessClaim, StringComparison.OrdinalIgnoreCase);
            if (!isRealm && !isResource)
                continue;

            if (!TryParseJsonObject(claim.Value, out var root))
                continue;

            var roles = isRealm ? ReadRolesArray(root) : ReadResourceRoles(root, clientId).SelectMany(x => x.Roles);
            foreach (var role in roles)
                yield return role;
        }
    }

    public static IEnumerable<string> FromJwt(string? jwt, string? clientId)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return [];

        JsonElement payload;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return [];

            using var doc = JsonDocument.Parse(Base64UrlEncoder.Decode(parts[1]));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            payload = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return [];
        }

        var roles = new List<string>();
        if (payload.TryGetProperty(RealmAccessClaim, out var realmAccess))
            roles.AddRange(ReadRolesArray(realmAccess));

        if (payload.TryGetProperty(ResourceAccessClaim, out var resourceAccess) && resourceAccess.ValueKind == JsonValueKind.Object)
            roles.AddRange(ReadResourceRoles(resourceAccess, clientId).SelectMany(x => x.Roles));

        return roles;
    }

    public static List<string> RealmRoles(ClaimsPrincipal user) =>
        user.FindAll(RealmAccessClaim)
            .SelectMany(c => TryParseJsonObject(c.Value, out var root) ? ReadRolesArray(root) : [])
            .ToList();

    public static Dictionary<string, List<string>> ResourceRolesByClient(ClaimsPrincipal user)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in user.FindAll(ResourceAccessClaim))
        {
            if (!TryParseJsonObject(claim.Value, out var root))
                continue;

            foreach (var (client, roles) in ReadResourceRoles(root, clientId: null))
            {
                if (!result.TryGetValue(client, out var list))
                    result[client] = list = new List<string>();

                foreach (var role in roles)
                {
                    if (!list.Contains(role, StringComparer.OrdinalIgnoreCase))
                        list.Add(role);
                }
            }
        }

        return result;
    }

    /// <summary>Adds each role as a ClaimTypes.Role claim unless the identity already has it.</summary>
    public static int AddRoleClaims(ClaimsIdentity identity, IEnumerable<string> roles)
    {
        var existing = new HashSet<string>(identity.FindAll(ClaimTypes.Role).Select(c => c.Value), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var role in roles)
        {
            if (existing.Add(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
                added++;
            }
        }

        return added;
    }

    private static IEnumerable<(string Client, IEnumerable<string> Roles)> ReadResourceRoles(JsonElement resourceAccess, string? clientId)
    {
        foreach (var property in resourceAccess.EnumerateObject())
        {
            if (!string.IsNullOrWhiteSpace(clientId)
                && !string.Equals(property.Name, clientId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(property.Name, "account", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return (property.Name, ReadRolesArray(property.Value).ToList());
        }
    }

    private static IEnumerable<string> ReadRolesArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("roles", out var roles)
            || roles.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind != JsonValueKind.String)
                continue;

            var value = role.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
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
