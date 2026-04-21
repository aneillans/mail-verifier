using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Exceptionless;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

var exceptionlessApiKey = Environment.GetEnvironmentVariable("Exceptionless__ApiKey")
    ?? Environment.GetEnvironmentVariable("EXCEPTIONLESS_API_KEY")
    ?? builder.Configuration["Exceptionless:ApiKey"];
var exceptionlessServerUrl = Environment.GetEnvironmentVariable("Exceptionless__ServerUrl")
    ?? Environment.GetEnvironmentVariable("EXCEPTIONLESS_SERVER_URL")
    ?? builder.Configuration["Exceptionless:ServerUrl"];
var exceptionlessEnabled = !string.IsNullOrWhiteSpace(exceptionlessApiKey);

VerificationResult.ConfigureAdditionalCommonMailboxNames(
    builder.Configuration.GetSection("Verification:AdditionalCommonMailboxNames").Get<string[]>());

// Add Razor Pages
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Error");
});

if (exceptionlessEnabled)
{
    builder.Services.AddExceptionless(options =>
    {
        options.ApiKey = exceptionlessApiKey!;
        if (!string.IsNullOrWhiteSpace(exceptionlessServerUrl))
            options.ServerUrl = exceptionlessServerUrl;
    });
}

// Authentication
builder.Services.AddAuthentication(options =>
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
    var oidcConfig = builder.Configuration.GetSection("OpenIdConnect");
    options.Authority = oidcConfig["Authority"];
    options.ClientId = oidcConfig["ClientId"];
    options.ClientSecret = oidcConfig["ClientSecret"];
    options.CallbackPath = oidcConfig["CallbackPath"] ?? "/signin-oidc";
    options.SignedOutCallbackPath = oidcConfig["SignedOutCallbackPath"] ?? "/signout-callback-oidc";
    
    // Prefer raw environment variables first to avoid accidental config-source precedence issues.
    var callbackScheme = Environment.GetEnvironmentVariable("OpenIdConnect__CallbackScheme")
        ?? builder.Configuration["OpenIdConnect:CallbackScheme"]
        ?? (builder.Environment.IsProduction() ? "https" : "http");
    var callbackHost = Environment.GetEnvironmentVariable("OpenIdConnect__CallbackHost")
        ?? builder.Configuration["OpenIdConnect:CallbackHost"];

    callbackScheme = callbackScheme.Trim().ToLowerInvariant();
    callbackHost = callbackHost?.Trim();
    
    if (!string.IsNullOrEmpty(callbackHost))
    {
        options.Events ??= new OpenIdConnectEvents();
        options.Events.OnRedirectToIdentityProvider += context =>
        {
            var scheme = callbackScheme;
            var host = callbackHost;
            context.ProtocolMessage.RedirectUri = $"{scheme}://{host}{options.CallbackPath}";
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("OpenIdConnect")
                .LogInformation("Using redirect_uri '{RedirectUri}' for OIDC challenge", context.ProtocolMessage.RedirectUri);
            return Task.CompletedTask;
        };
    }
    
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.ClaimActions.MapJsonKey("realm_access", "realm_access");
    options.ClaimActions.MapJsonKey("resource_access", "resource_access");
    options.TokenValidationParameters = new TokenValidationParameters
    {
        NameClaimType = "preferred_username",
        RoleClaimType = ClaimTypes.Role
    };

    options.Events ??= new OpenIdConnectEvents();
    var existingOnTokenValidated = options.Events.OnTokenValidated;
    options.Events.OnTokenValidated = async context =>
    {
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            var rolesBefore = identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            AddKeycloakRoleClaims(identity, context.Principal.Claims, options.ClientId);

            // Fallback: Keycloak roles are often present only in raw token JSON payloads.
            AddKeycloakRoleClaimsFromJwt(identity, context.ProtocolMessage?.IdToken, options.ClientId);
            AddKeycloakRoleClaimsFromJwt(identity, context.TokenEndpointResponse?.AccessToken, options.ClientId);

            var rolesAfter = identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            LogOidcAuthSummary(
                context.HttpContext,
                context.Principal,
                options.ClientId,
                stage: "OnTokenValidated",
                addedRoleCount: rolesAfter - rolesBefore,
                hasIdToken: !string.IsNullOrWhiteSpace(context.ProtocolMessage?.IdToken),
                hasAccessToken: !string.IsNullOrWhiteSpace(context.TokenEndpointResponse?.AccessToken));
        }

        if (existingOnTokenValidated != null)
        {
            await existingOnTokenValidated(context);
        }
    };

    var existingOnUserInfoReceived = options.Events.OnUserInformationReceived;
    options.Events.OnUserInformationReceived = async context =>
    {
        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            var rolesBefore = identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            AddKeycloakRoleClaims(identity, context.Principal.Claims, options.ClientId);

            if (context.Properties != null)
            {
                AddKeycloakRoleClaimsFromJwt(identity, context.Properties.GetTokenValue("id_token"), options.ClientId);
                AddKeycloakRoleClaimsFromJwt(identity, context.Properties.GetTokenValue("access_token"), options.ClientId);
            }

            var rolesAfter = identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            LogOidcAuthSummary(
                context.HttpContext,
                context.Principal,
                options.ClientId,
                stage: "OnUserInformationReceived",
                addedRoleCount: rolesAfter - rolesBefore,
                hasIdToken: context.Properties?.GetTokenValue("id_token") is { Length: > 0 },
                hasAccessToken: context.Properties?.GetTokenValue("access_token") is { Length: > 0 });
        }

        if (existingOnUserInfoReceived != null)
        {
            await existingOnUserInfoReceived(context);
        }
    };

    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
});

static void AddKeycloakRoleClaims(ClaimsIdentity identity, IEnumerable<Claim> claims, string? clientId)
{
    var existing = new HashSet<string>(
        identity.FindAll(ClaimTypes.Role).Select(c => c.Value),
        StringComparer.OrdinalIgnoreCase);

    foreach (var role in ExtractKeycloakRoles(claims, clientId))
    {
        if (existing.Add(role))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }
}

static void AddKeycloakRoleClaimsFromJwt(ClaimsIdentity identity, string? jwt, string? clientId)
{
    if (string.IsNullOrWhiteSpace(jwt))
        return;

    var existing = new HashSet<string>(
        identity.FindAll(ClaimTypes.Role).Select(c => c.Value),
        StringComparer.OrdinalIgnoreCase);

    foreach (var role in ExtractKeycloakRolesFromJwt(jwt, clientId))
    {
        if (existing.Add(role))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
    }
}

static IEnumerable<string> ExtractKeycloakRoles(IEnumerable<Claim> claims, string? clientId)
{
    foreach (var claim in claims)
    {
        if (!string.Equals(claim.Type, "realm_access", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(claim.Type, "resource_access", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!TryParseJsonObject(claim.Value, out var root))
        {
            continue;
        }

        if (string.Equals(claim.Type, "realm_access", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var role in ReadRolesArray(root))
                yield return role;
        }
        else
        {
            foreach (var role in ReadResourceRoles(root, clientId))
                yield return role;
        }
    }
}

static IEnumerable<string> ExtractKeycloakRolesFromJwt(string jwt, string? clientId)
{
    JsonElement payload;
    try
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            yield break;

        var payloadJson = Base64UrlEncoder.Decode(parts[1]);
        using var doc = JsonDocument.Parse(payloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            yield break;

        payload = doc.RootElement.Clone();
    }
    catch
    {
        yield break;
    }

    if (payload.TryGetProperty("realm_access", out var realmAccess))
    {
        foreach (var role in ReadRolesArray(realmAccess))
            yield return role;
    }

    if (payload.TryGetProperty("resource_access", out var resourceAccess)
        && resourceAccess.ValueKind == JsonValueKind.Object)
    {
        foreach (var role in ReadResourceRoles(resourceAccess, clientId))
            yield return role;
    }
}

static void LogOidcAuthSummary(
    HttpContext httpContext,
    ClaimsPrincipal? principal,
    string? clientId,
    string stage,
    int addedRoleCount,
    bool hasIdToken,
    bool hasAccessToken)
{
    var logger = httpContext.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("OpenIdConnect.AuthDebug");

    if (principal?.Identity is not ClaimsIdentity identity)
    {
        logger.LogWarning("OIDC {Stage}: principal identity missing; cannot summarize claims", stage);
        return;
    }

    var roles = identity.FindAll(ClaimTypes.Role)
        .Select(c => c.Value)
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
        .ToList();

    var username = principal.FindFirst("preferred_username")?.Value
        ?? principal.Identity?.Name
        ?? "(unknown)";
    var subject = principal.FindFirst("sub")?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? "(missing)";

    var hasRealmAccessClaim = identity.HasClaim(c => string.Equals(c.Type, "realm_access", StringComparison.OrdinalIgnoreCase));
    var hasResourceAccessClaim = identity.HasClaim(c => string.Equals(c.Type, "resource_access", StringComparison.OrdinalIgnoreCase));

    logger.LogInformation(
        "OIDC {Stage}: user={User} sub={Sub} claimCount={ClaimCount} roleCount={RoleCount} addedRoleCount={AddedRoleCount} roles=[{Roles}] hasRealmAccessClaim={HasRealmAccessClaim} hasResourceAccessClaim={HasResourceAccessClaim} hasIdToken={HasIdToken} hasAccessToken={HasAccessToken} clientId={ClientId}",
        stage,
        username,
        subject,
        identity.Claims.Count(),
        roles.Count,
        addedRoleCount,
        string.Join(",", roles),
        hasRealmAccessClaim,
        hasResourceAccessClaim,
        hasIdToken,
        hasAccessToken,
        clientId ?? "(null)");
}

static IEnumerable<string> ReadResourceRoles(JsonElement resourceAccess, string? clientId)
{
    foreach (var property in resourceAccess.EnumerateObject())
    {
        if (!string.IsNullOrWhiteSpace(clientId)
            && !string.Equals(property.Name, clientId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(property.Name, "account", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        foreach (var role in ReadRolesArray(property.Value))
            yield return role;
    }
}

static IEnumerable<string> ReadRolesArray(JsonElement element)
{
    if (!element.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array)
    {
        yield break;
    }

    foreach (var role in roles.EnumerateArray())
    {
        if (role.ValueKind != JsonValueKind.String)
            continue;

        var value = role.GetString();
        if (!string.IsNullOrWhiteSpace(value))
            yield return value;
    }
}

static bool TryParseJsonObject(string value, out JsonElement root)
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

// EF Core / SQLite
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// App services
builder.Services.AddScoped<EmailVerificationService>();
builder.Services.AddSingleton<VerificationQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VerificationQueueService>());
builder.Services.AddHostedService<DataRetentionService>();

// Antiforgery
builder.Services.AddAntiforgery();

var app = builder.Build();

// Ensure DB created and re-enqueue any jobs that were interrupted on last shutdown
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // If the DB was previously created via EnsureCreated() it has no __EFMigrationsHistory
    // table. Seed the history with the migrations that match the already-existing schema so
    // that Migrate() only runs genuinely new migrations (e.g. AddJobName).
    var conn = db.Database.GetDbConnection();
    conn.Open();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
        var historyExists = (long)cmd.ExecuteScalar()! > 0;

        if (!historyExists)
        {
            cmd.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();

            // Check whether the original tables are already present (EnsureCreated path).
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='VerificationJobs'";
            var tablesExist = (long)cmd.ExecuteScalar()! > 0;

            if (tablesExist)
            {
                cmd.CommandText = """
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260331194128_AddRetestTracking', '10.0.5');
                    """;
                cmd.ExecuteNonQuery();
            }
        }

    }
    conn.Close();

    var pendingMigrations = db.Database.GetPendingMigrations().ToList();
    if (pendingMigrations.Count > 0)
    {
        app.Logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}",
            pendingMigrations.Count,
            string.Join(", ", pendingMigrations));
        db.Database.Migrate();
    }

    var staleJobs = db.VerificationJobs
        .Where(j => j.Status == "Pending" || j.Status == "Processing")
        .Select(j => j.Id)
        .ToList();

    if (staleJobs.Count > 0)
    {
        var queue = app.Services.GetRequiredService<VerificationQueueService>();
        foreach (var jobId in staleJobs)
        {
            // Reset ProcessedEmails so the batch loop starts clean
            var job = db.VerificationJobs.Find(jobId)!;
            job.Status = "Pending";
            job.ProcessedEmails = 0;
            queue.EnqueueJob(jobId);
        }
        db.SaveChanges();
        app.Logger.LogInformation("Re-enqueued {Count} stale job(s) from previous run", staleJobs.Count);
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

if (exceptionlessEnabled)
{
    app.UseExceptionless();
    app.Logger.LogInformation(
        "Exceptionless initialized from configuration. ServerUrl={ServerUrl}",
        string.IsNullOrWhiteSpace(exceptionlessServerUrl) ? "(default)" : exceptionlessServerUrl);
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Live progress endpoint polled by the job details page
app.MapGet("/api/jobs/{id:int}/progress", async (int id, AppDbContext db, HttpContext ctx) =>
{
    var query = db.VerificationJobs
        .Include(j => j.Results)
        .Where(j => j.Id == id)
        .AsQueryable();

    if (!UserAccess.IsAdmin(ctx.User))
    {
        var userId = UserAccess.GetUserId(ctx.User);
        if (string.IsNullOrWhiteSpace(userId))
            return Results.NotFound();

        query = query.Where(j => j.UploadedByUser == userId);
    }

    var job = await query.FirstOrDefaultAsync();

    if (job == null)
        return Results.NotFound();

    return Results.Json(new
    {
        job.Status,
        job.TotalEmails,
        job.ProcessedEmails,
        CreatedAtUtc = new DateTimeOffset(job.CreatedAt.ToUniversalTime()).ToUnixTimeMilliseconds(),
        Results = job.Results.OrderBy(r => r.EmailAddress).Select(r => new
        {
            r.Id,
            r.EmailAddress,
            r.DomainExists,
            r.HasMxRecords,
            r.MailboxExists,
            r.IsCommonMailbox,
            r.IsAtRisk,
            r.IsPotentialSoftFailure,
            r.SoftFailureNote,
            r.IsRetryable,
            r.IsVerified,
            r.ErrorMessage,
            r.SmtpLog,
            VerifiedAt = r.VerifiedAt.ToLocalTime().ToString("HH:mm:ss")
        })
    });
}).RequireAuthorization();

app.MapGet("/signin", (HttpContext ctx) =>
    ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" }));

app.MapPost("/signout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});

app.Run();
