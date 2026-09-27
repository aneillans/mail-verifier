using System.Collections.Concurrent;
using System.Net;
using DnsClient;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Services.Verification;

public sealed record MxLookupResult(bool DomainExists, IReadOnlyList<string> MxHosts, string? Error = null)
{
    public bool HasMx => MxHosts.Count > 0;
}

public interface IMxResolver
{
    Task<MxLookupResult> ResolveAsync(string domain, CancellationToken ct);
}

/// <summary>
/// Resolves MX hosts with an application-wide cache. The cache stores the lookup task, so concurrent
/// requests for the same domain share one DNS query.
/// </summary>
public sealed class DnsMxResolver : IMxResolver
{
    private static readonly TimeSpan FailureTtl = TimeSpan.FromMinutes(1);
    private const int PruneThreshold = 20000;

    private readonly ILookupClient _dns;
    private readonly ILogger<DnsMxResolver> _logger;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(Lazy<Task<MxLookupResult>> Lookup, DateTimeOffset ExpiresAt);

    public DnsMxResolver(ILookupClient dns, IOptions<VerificationOptions> options, ILogger<DnsMxResolver> logger)
    {
        _dns = dns;
        _logger = logger;
        _ttl = TimeSpan.FromMinutes(Math.Max(1, options.Value.DnsCacheMinutes));
    }

    public async Task<MxLookupResult> ResolveAsync(string domain, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cache.Count > PruneThreshold)
            Prune(now);

        var created = new CacheEntry(new Lazy<Task<MxLookupResult>>(() => LookupAsync(domain)), now + _ttl);
        var entry = _cache.AddOrUpdate(domain, created, (_, existing) => existing.ExpiresAt > now ? existing : created);

        var result = await entry.Lookup.Value.WaitAsync(ct);
        if (result.Error != null && entry.ExpiresAt > now + FailureTtl)
        {
            // Don't pin transient DNS failures for the full cache lifetime.
            _cache.TryUpdate(domain, entry with { ExpiresAt = now + FailureTtl }, entry);
        }

        return result;
    }

    private async Task<MxLookupResult> LookupAsync(string domain)
    {
        try
        {
            var response = await _dns.QueryAsync(domain, QueryType.MX);
            if (response.HasError)
            {
                return response.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain
                    ? new MxLookupResult(false, [])
                    : new MxLookupResult(false, [], $"DNS lookup failed: {response.ErrorMessage}");
            }

            var hosts = response.Answers.MxRecords()
                .OrderBy(r => r.Preference)
                .Select(r => r.Exchange.Value.TrimEnd('.'))
                .Where(h => h.Length > 0) // RFC 7505 null MX ("MX 0 .") means the domain accepts no mail
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (hosts.Count > 0)
                return new MxLookupResult(true, hosts);

            return new MxLookupResult(await HostResolvesAsync(domain), []);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MX lookup failed for {Domain}", domain);
            return new MxLookupResult(false, [], $"DNS lookup failed: {ex.Message}");
        }
    }

    private static async Task<bool> HostResolvesAsync(string domain)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(domain);
            return entry.AddressList.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _cache)
        {
            if (entry.ExpiresAt <= now)
                _cache.TryRemove(key, out _);
        }
    }
}
