using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Debug;

[Authorize]
public class ClaimsModel : PageModel
{
    public List<ClaimRow> Claims { get; private set; } = new();

    public string? NameIdentifier { get; private set; }

    public string? Subject { get; private set; }

    public bool IsAdminFromHelper { get; private set; }

    public bool IsInRoleAdminLower { get; private set; }

    public bool IsInRoleAdminUpper { get; private set; }

    public List<string> StandardRoleClaims { get; private set; } = new();

    public List<string> RealmRoles { get; private set; } = new();

    public Dictionary<string, List<string>> ResourceRoles { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public void OnGet()
    {
        Claims = User.Claims
            .Select(c => new ClaimRow(c.Type, c.Value, c.Issuer, c.ValueType))
            .OrderBy(c => c.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        NameIdentifier = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Subject = User.FindFirst("sub")?.Value;

        IsInRoleAdminLower = User.IsInRole("admin");
        IsInRoleAdminUpper = User.IsInRole("Admin");
        IsAdminFromHelper = UserAccess.IsAdmin(User);

        StandardRoleClaims = ExtractStandardRoles(User).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        RealmRoles = ExtractRealmRoles(User).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        ResourceRoles = ExtractResourceRoles(User)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractStandardRoles(ClaimsPrincipal user)
    {
        foreach (var claim in user.Claims)
        {
            if (claim.Type != ClaimTypes.Role && claim.Type != "role" && claim.Type != "roles")
                continue;

            var values = claim.Value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    yield return value.Trim();
            }
        }
    }

    private static IEnumerable<string> ExtractRealmRoles(ClaimsPrincipal user)
    {
        foreach (var claim in user.FindAll("realm_access"))
        {
            if (!TryParseJsonObject(claim.Value, out var json))
                continue;

            foreach (var role in ReadRoles(json))
                yield return role;
        }
    }

    private static Dictionary<string, List<string>> ExtractResourceRoles(ClaimsPrincipal user)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in user.FindAll("resource_access"))
        {
            if (!TryParseJsonObject(claim.Value, out var json))
                continue;

            foreach (var client in json.EnumerateObject())
            {
                if (!result.TryGetValue(client.Name, out var roles))
                {
                    roles = new List<string>();
                    result[client.Name] = roles;
                }

                foreach (var role in ReadRoles(client.Value))
                {
                    if (!roles.Contains(role, StringComparer.OrdinalIgnoreCase))
                        roles.Add(role);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> ReadRoles(JsonElement element)
    {
        if (!element.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var role in roles.EnumerateArray())
        {
            if (role.ValueKind != JsonValueKind.String)
                continue;

            var value = role.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                yield return value.Trim();
        }
    }

    private static bool TryParseJsonObject(string rawValue, out JsonElement json)
    {
        json = default;

        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(rawValue);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            json = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public record ClaimRow(string Type, string Value, string Issuer, string ValueType);
}