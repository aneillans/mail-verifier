using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Debug;

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

        StandardRoleClaims = UserAccess.StandardRoles(User).Order(StringComparer.OrdinalIgnoreCase).ToList();
        RealmRoles = KeycloakRoles.RealmRoles(User).Order(StringComparer.OrdinalIgnoreCase).ToList();
        ResourceRoles = KeycloakRoles.ResourceRolesByClient(User)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public record ClaimRow(string Type, string Value, string Issuer, string ValueType);
}