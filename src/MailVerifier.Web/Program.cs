using DnsClient;
using Exceptionless;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Endpoints;
using MailVerifier.Web.Health;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;
using MailVerifier.Web.Services.Verification;

if (args.Contains("--healthcheck"))
    return await HealthEndpoints.RunProbeAsync();

var builder = WebApplication.CreateBuilder(args);

var exceptionlessApiKey = builder.Configuration["Exceptionless:ApiKey"]
    ?? Environment.GetEnvironmentVariable("EXCEPTIONLESS_API_KEY");
var exceptionlessServerUrl = builder.Configuration["Exceptionless:ServerUrl"]
    ?? Environment.GetEnvironmentVariable("EXCEPTIONLESS_SERVER_URL");
var exceptionlessEnabled = !string.IsNullOrWhiteSpace(exceptionlessApiKey);

VerificationResult.ConfigureAdditionalCommonMailboxNames(
    builder.Configuration.GetSection("Verification:AdditionalCommonMailboxNames").Get<string[]>());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AuthorizeFolder("/Admin", UserAccess.AdminPolicy);
    // The claims debug page helps diagnose role mapping; outside development only admins may see it.
    if (!builder.Environment.IsDevelopment())
        options.Conventions.AuthorizeFolder("/Debug", UserAccess.AdminPolicy);
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

builder.Services.AddOidcAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(UserAccess.AdminPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(ctx => UserAccess.IsAdmin(ctx.User)));

// EF Core / PostgreSQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Verification
builder.Services.Configure<VerificationOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<ILookupClient>(_ => new LookupClient(new LookupClientOptions
{
    UseCache = true,
    Timeout = TimeSpan.FromSeconds(5),
    Retries = 2
}));
builder.Services.AddSingleton<IMxResolver, DnsMxResolver>();
builder.Services.AddSingleton<SmtpConnectionLimiter>();
builder.Services.AddSingleton<EmailVerificationService>();
builder.Services.AddSingleton<VerificationQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VerificationQueueService>());
builder.Services.AddHostedService<DataRetentionService>();
builder.Services.AddScoped<JobCreationService>();

builder.Services.AddAppHealthChecks();
builder.Services.AddAntiforgery();

var app = builder.Build();

// Apply migrations and re-enqueue jobs interrupted by the last shutdown. They resume where they left
// off: addresses that already have a result are skipped.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var pendingMigrations = db.Database.GetPendingMigrations().ToList();
    if (pendingMigrations.Count > 0)
    {
        app.Logger.LogInformation(
            "Applying {Count} pending migration(s): {Migrations}",
            pendingMigrations.Count,
            string.Join(", ", pendingMigrations));
        db.Database.Migrate();
    }

    db.VerificationJobs
        .Where(j => j.Status == JobStatus.Processing)
        .ExecuteUpdate(s => s.SetProperty(j => j.Status, JobStatus.Pending));

    var staleJobs = db.VerificationJobs
        .Where(j => j.Status == JobStatus.Pending)
        .OrderBy(j => j.CreatedAt)
        .Select(j => j.Id)
        .ToList();

    if (staleJobs.Count > 0)
    {
        var queue = app.Services.GetRequiredService<VerificationQueueService>();
        foreach (var jobId in staleJobs)
            queue.EnqueueJob(jobId);

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

app.MapAppHealthChecks();
app.MapRazorPages();
app.MapJobApi();

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
return 0;
