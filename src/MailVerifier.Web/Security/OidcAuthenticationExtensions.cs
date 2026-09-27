using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MailVerifier.Web.Security;

public static class OidcAuthenticationExtensions
{
    /// <summary>Cookie + Keycloak OpenID Connect sign-in, with Keycloak roles mapped to role claims.</summary>
    public static IServiceCollection AddOidcAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(options =>
        {
            options.LoginPath = "/";
            options.AccessDeniedPath = "/Error";
        })
        .AddOpenIdConnect(options =>
        {
            var oidcConfig = configuration.GetSection("OpenIdConnect");
            options.Authority = oidcConfig["Authority"];
            options.ClientId = oidcConfig["ClientId"];
            options.ClientSecret = oidcConfig["ClientSecret"];
            options.CallbackPath = oidcConfig["CallbackPath"] ?? "/signin-oidc";
            options.SignedOutCallbackPath = oidcConfig["SignedOutCallbackPath"] ?? "/signout-callback-oidc";
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.ClaimActions.MapJsonKey(KeycloakRoles.RealmAccessClaim, KeycloakRoles.RealmAccessClaim);
            options.ClaimActions.MapJsonKey(KeycloakRoles.ResourceAccessClaim, KeycloakRoles.ResourceAccessClaim);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = "preferred_username",
                RoleClaimType = ClaimTypes.Role
            };
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");

            var callbackScheme = (configuration["OpenIdConnect:CallbackScheme"]
                ?? (environment.IsProduction() ? "https" : "http")).Trim().ToLowerInvariant();
            var callbackHost = configuration["OpenIdConnect:CallbackHost"]?.Trim();

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    if (!string.IsNullOrEmpty(callbackHost))
                    {
                        context.ProtocolMessage.RedirectUri = $"{callbackScheme}://{callbackHost}{options.CallbackPath}";
                        GetLogger(context.HttpContext, "OpenIdConnect")
                            .LogInformation("Using redirect_uri '{RedirectUri}' for OIDC challenge", context.ProtocolMessage.RedirectUri);
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    // Keycloak roles are often present only in the raw token payloads.
                    MapRoles(
                        context.HttpContext,
                        context.Principal,
                        options.ClientId,
                        "OnTokenValidated",
                        context.ProtocolMessage?.IdToken,
                        context.TokenEndpointResponse?.AccessToken);
                    return Task.CompletedTask;
                },
                OnUserInformationReceived = context =>
                {
                    MapRoles(
                        context.HttpContext,
                        context.Principal,
                        options.ClientId,
                        "OnUserInformationReceived",
                        context.Properties?.GetTokenValue("id_token"),
                        context.Properties?.GetTokenValue("access_token"));
                    return Task.CompletedTask;
                }
            };
        });

        return services;
    }

    private static void MapRoles(HttpContext httpContext, ClaimsPrincipal? principal, string? clientId, string stage, string? idToken, string? accessToken)
    {
        if (principal?.Identity is not ClaimsIdentity identity)
            return;

        var added = KeycloakRoles.AddRoleClaims(identity, KeycloakRoles.FromClaims(principal.Claims, clientId))
            + KeycloakRoles.AddRoleClaims(identity, KeycloakRoles.FromJwt(idToken, clientId))
            + KeycloakRoles.AddRoleClaims(identity, KeycloakRoles.FromJwt(accessToken, clientId));

        var roles = identity.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        GetLogger(httpContext, "OpenIdConnect.AuthDebug").LogInformation(
            "OIDC {Stage}: user={User} sub={Sub} claimCount={ClaimCount} addedRoleCount={AddedRoleCount} roles=[{Roles}] hasIdToken={HasIdToken} hasAccessToken={HasAccessToken} clientId={ClientId}",
            stage,
            principal.FindFirst("preferred_username")?.Value ?? identity.Name ?? "(unknown)",
            principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(missing)",
            identity.Claims.Count(),
            added,
            string.Join(",", roles),
            !string.IsNullOrWhiteSpace(idToken),
            !string.IsNullOrWhiteSpace(accessToken),
            clientId ?? "(null)");
    }

    private static ILogger GetLogger(HttpContext httpContext, string category) =>
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(category);
}
