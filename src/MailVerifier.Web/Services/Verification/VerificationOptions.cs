namespace MailVerifier.Web.Services.Verification;

/// <summary>Settings bound from the "Smtp" configuration section.</summary>
public sealed class VerificationOptions
{
    public const string DefaultEhloHost = "mailverifier.local";

    public string? EhloHost { get; set; }

    public string? MailFromAddress { get; set; }

    public int Port { get; set; } = 25;

    /// <summary>Timeout applied to the TCP connect and to each SMTP command.</summary>
    public int CommandTimeoutMs { get; set; } = 10000;

    /// <summary>Maximum RCPT TO probes sent over one SMTP session.</summary>
    public int MaxRecipientsPerSession { get; set; } = 20;

    /// <summary>Maximum SMTP sessions open at once across all MX hosts.</summary>
    public int MaxParallelSessions { get; set; } = 10;

    /// <summary>Limit for MX hosts with no ConnectionLimits rule. 0 disables it.</summary>
    public int DefaultConnectionLimitPerMxHost { get; set; } = 5;

    /// <summary>How many MX hosts (by preference) to try before giving up on a connection.</summary>
    public int MaxMxHostsToTry { get; set; } = 3;

    public int DnsCacheMinutes { get; set; } = 30;

    public bool CatchAllDetection { get; set; } = true;

    public string EffectiveEhloHost =>
        string.IsNullOrWhiteSpace(EhloHost) ? DefaultEhloHost : EhloHost.Trim();

    public string EffectiveMailFromAddress =>
        string.IsNullOrWhiteSpace(MailFromAddress) ? $"verify@{EffectiveEhloHost}" : MailFromAddress.Trim();
}
