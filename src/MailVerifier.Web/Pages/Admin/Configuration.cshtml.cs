using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using MailVerifier.Web.Services.Verification;
using Npgsql;

namespace MailVerifier.Web.Pages.Admin;

public class ConfigurationModel : PageModel
{
    private readonly IConfiguration _configuration;
    private readonly SmtpConnectionLimiter _limiter;
    private readonly VerificationOptions _smtp;

    public Dictionary<string, object> DisplayConfig { get; set; } = new();

    public ConfigurationModel(IConfiguration configuration, SmtpConnectionLimiter limiter, IOptions<VerificationOptions> smtp)
    {
        _configuration = configuration;
        _limiter = limiter;
        _smtp = smtp.Value;
    }

    public void OnGet()
    {
        BuildDisplayConfig();
    }

    private void BuildDisplayConfig()
    {
        // Environment variables (safe ones)
        DisplayConfig["Environment"] = new Dictionary<string, string>
        {
            { "ASPNETCORE_ENVIRONMENT", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "N/A" },
            { "ASPNETCORE_URLS", Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "N/A" }
        };

        // OpenIdConnect configuration (non-sensitive)
        var oidcConfig = _configuration.GetSection("OpenIdConnect");
        DisplayConfig["OpenIdConnect"] = new Dictionary<string, string>
        {
            { "Authority", oidcConfig["Authority"] ?? "N/A" },
            { "ClientId", oidcConfig["ClientId"] ?? "N/A" },
            { "CallbackScheme", oidcConfig["CallbackScheme"] ?? "N/A" },
            { "CallbackHost", oidcConfig["CallbackHost"] ?? "N/A" },
            { "CallbackPath", oidcConfig["CallbackPath"] ?? "N/A" },
            { "SignedOutCallbackPath", oidcConfig["SignedOutCallbackPath"] ?? "N/A" }
        };

        // Data Retention
        var retentionConfig = _configuration.GetSection("DataRetention");
        DisplayConfig["DataRetention"] = new Dictionary<string, string>
        {
            { "RetentionDays", retentionConfig["RetentionDays"] ?? "N/A" }
        };

        // SMTP Configuration (effective values, as used by the verifier)
        DisplayConfig["Smtp"] = new Dictionary<string, string>
        {
            { "EhloHost", _smtp.EffectiveEhloHost },
            { "MailFromAddress", _smtp.EffectiveMailFromAddress },
            { "Port", _smtp.Port.ToString() },
            { "CommandTimeoutMs", _smtp.CommandTimeoutMs.ToString() },
            { "MaxParallelSessions", _smtp.MaxParallelSessions.ToString() },
            { "MaxRecipientsPerSession", _smtp.MaxRecipientsPerSession.ToString() },
            { "MaxMxHostsToTry", _smtp.MaxMxHostsToTry.ToString() },
            { "CatchAllDetection", _smtp.CatchAllDetection.ToString() },
            { "DnsCacheMinutes", _smtp.DnsCacheMinutes.ToString() },
            { "ConnectionLimits", string.Join(", ", _limiter.Rules.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")) },
            { "DefaultConnectionLimitPerMxHost", _limiter.DefaultLimitPerMxHost > 0 ? _limiter.DefaultLimitPerMxHost.ToString() : "unlimited" }
        };

        // Logging
        var loggingConfig = _configuration.GetSection("Logging:LogLevel");
        DisplayConfig["Logging"] = new Dictionary<string, string>
        {
            { "Default", loggingConfig["Default"] ?? "N/A" },
            { "Microsoft.AspNetCore", loggingConfig["Microsoft.AspNetCore"] ?? "N/A" }
        };

        // Database (mask credentials)
        var connString = _configuration.GetConnectionString("DefaultConnection");
        var displayConnString = MaskSensitiveData(connString);
        DisplayConfig["Database"] = new Dictionary<string, string>
        {
            { "ConnectionString", displayConnString ?? "N/A" }
        };
    }

    private string MaskSensitiveData(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(value);
            if (!string.IsNullOrEmpty(builder.Password))
                builder.Password = "********";
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return "(unparseable connection string)";
        }
    }
}
