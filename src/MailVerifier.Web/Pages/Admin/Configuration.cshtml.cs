using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MailVerifier.Web.Security;
using System.Collections;
using System.Collections.Generic;

namespace MailVerifier.Web.Pages.Admin;

[Authorize]
public class ConfigurationModel : PageModel
{
    private static readonly Dictionary<string, int> DefaultConnectionLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hotmail.com"] = 2,
        ["outlook.com"] = 2,
        ["live.com"] = 2,
        ["msn.com"] = 2
    };

    private readonly IConfiguration _configuration;

    public Dictionary<string, object> DisplayConfig { get; set; } = new();

    public ConfigurationModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IActionResult OnGet()
    {
        // Check if user is admin
        if (!UserAccess.IsAdmin(User))
        {
            return Forbid();
        }

        BuildDisplayConfig();
        return Page();
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

        // SMTP Configuration
        var smtpConfig = _configuration.GetSection("Smtp");
        var connectionLimits = BuildEffectiveConnectionLimits(smtpConfig);
        DisplayConfig["Smtp"] = new Dictionary<string, string>
        {
            { "EhloHost", Environment.GetEnvironmentVariable("Smtp__EhloHost") ?? smtpConfig["EhloHost"] ?? "N/A" },
            { "MailFromAddress", Environment.GetEnvironmentVariable("Smtp__MailFromAddress") ?? smtpConfig["MailFromAddress"] ?? "N/A" },
            { "ConnectionLimits", connectionLimits.Count > 0 ? string.Join(", ", connectionLimits.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")) : "N/A" }
        };

        // Logging
        var loggingConfig = _configuration.GetSection("Logging:LogLevel");
        DisplayConfig["Logging"] = new Dictionary<string, string>
        {
            { "Default", loggingConfig["Default"] ?? "N/A" },
            { "Microsoft.AspNetCore", loggingConfig["Microsoft.AspNetCore"] ?? "N/A" }
        };

        // Database (show path but mask credentials)
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

        // For connection strings, just show the path (SQLite case)
        if (value.Contains("Data Source="))
        {
            var parts = value.Split(';');
            return parts[0]; // Just the data source part
        }

        return value;
    }

    private Dictionary<string, int> BuildEffectiveConnectionLimits(IConfigurationSection smtpConfig)
    {
        var limits = new Dictionary<string, int>(DefaultConnectionLimits, StringComparer.OrdinalIgnoreCase);

        foreach (var child in smtpConfig.GetSection("ConnectionLimits").GetChildren())
        {
            var domain = NormalizeDomainKey(child.Key);
            if (domain == null)
                continue;

            if (int.TryParse(child.Value, out var maxConnections) && maxConnections > 0)
            {
                limits[domain] = maxConnections;
            }
        }

        const string envPrefix = "Smtp__ConnectionLimits__";
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || !key.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var domain = NormalizeDomainKey(key[envPrefix.Length..]);
            if (domain == null)
                continue;

            if (entry.Value is string rawValue && int.TryParse(rawValue, out var maxConnections) && maxConnections > 0)
            {
                limits[domain] = maxConnections;
            }
        }

        return limits;
    }

    private static string? NormalizeDomainKey(string? value)
    {
        var domain = value?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(domain) ? null : domain;
    }
}
