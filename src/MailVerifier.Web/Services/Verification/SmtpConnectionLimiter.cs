using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Services.Verification;

/// <summary>
/// Caps concurrent SMTP sessions. Rules in Smtp:ConnectionLimits are domain suffixes matched first
/// against the MX host (so custom domains hosted by Microsoft/Google are covered) and then against the
/// recipient domain. MX hosts with no rule get Smtp:DefaultConnectionLimitPerMxHost.
/// </summary>
public sealed class SmtpConnectionLimiter
{
    public static readonly IReadOnlyDictionary<string, int> DefaultRules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        // Consumer Microsoft mailboxes (MX *.olc.protection.outlook.com) are aggressively rate limited.
        ["outlook.com"] = 2,
        ["hotmail.com"] = 2,
        ["live.com"] = 2,
        ["msn.com"] = 2,
        // Microsoft 365 tenants (MX <tenant>.mail.protection.outlook.com).
        ["mail.protection.outlook.com"] = 4
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, int>> _rulesLongestFirst;

    public IReadOnlyDictionary<string, int> Rules { get; }

    public int DefaultLimitPerMxHost { get; }

    public SmtpConnectionLimiter(IConfiguration configuration, IOptions<VerificationOptions> options)
    {
        var rules = new Dictionary<string, int>(DefaultRules, StringComparer.OrdinalIgnoreCase);

        // Environment variables such as Smtp__ConnectionLimits__gmail.com=3 land in this section too.
        foreach (var child in configuration.GetSection("Smtp:ConnectionLimits").GetChildren())
        {
            var domain = NormalizeDomain(child.Key);
            if (domain != null && int.TryParse(child.Value, out var max) && max > 0)
                rules[domain] = max;
        }

        Rules = rules;
        _rulesLongestFirst = rules.OrderByDescending(r => r.Key.Length).ToList();
        DefaultLimitPerMxHost = Math.Max(0, options.Value.DefaultConnectionLimitPerMxHost);
    }

    /// <summary>Returns the limiter key and limit for a session, or (null, 0) when unthrottled.</summary>
    public (string? Key, int Limit) Resolve(string mxHost, IEnumerable<string> recipientDomains)
    {
        if (TryMatch(mxHost, out var rule))
            return (rule.Key, rule.Value);

        foreach (var domain in recipientDomains)
        {
            if (TryMatch(domain, out rule))
                return (rule.Key, rule.Value);
        }

        return DefaultLimitPerMxHost > 0
            ? ("mx:" + mxHost.ToLowerInvariant(), DefaultLimitPerMxHost)
            : (null, 0);
    }

    public async Task<IDisposable> AcquireAsync(string key, int limit, CancellationToken ct)
    {
        var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(limit, limit));
        await semaphore.WaitAsync(ct);
        return new Lease(semaphore);
    }

    /// <summary>True when host equals the pattern or is a subdomain of it ("mx.live.com" but not "alive.com").</summary>
    public static bool MatchesDomain(string host, string pattern)
    {
        host = host.Trim().TrimEnd('.');
        return host.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || (host.Length > pattern.Length
                && host.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)
                && host[host.Length - pattern.Length - 1] == '.');
    }

    private bool TryMatch(string host, out KeyValuePair<string, int> rule)
    {
        foreach (var candidate in _rulesLongestFirst)
        {
            if (MatchesDomain(host, candidate.Key))
            {
                rule = candidate;
                return true;
            }
        }

        rule = default;
        return false;
    }

    private static string? NormalizeDomain(string? value)
    {
        var domain = value?.Trim().Trim('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(domain) ? null : domain;
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
    }
}
