using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using MailVerifier.Web.Models;
using MailVerifier.Web.Services.Verification;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Services;

/// <summary>
/// Verifies addresses by resolving MX records and probing with RCPT TO. Addresses sharing a primary MX host
/// are probed over one SMTP session (up to Smtp:MaxRecipientsPerSession), sessions run in parallel up to
/// Smtp:MaxParallelSessions, and each session holds a connection-limit lease for its MX host.
/// </summary>
public class EmailVerificationService
{
    private static readonly TimeSpan CatchAllCacheTtl = TimeSpan.FromHours(6);

    private readonly ILogger<EmailVerificationService> _logger;
    private readonly IMxResolver _mxResolver;
    private readonly SmtpConnectionLimiter _limiter;
    private readonly VerificationOptions _options;
    private readonly ConcurrentDictionary<string, (bool IsCatchAll, DateTimeOffset ExpiresAt)> _catchAllCache = new(StringComparer.OrdinalIgnoreCase);

    public EmailVerificationService(
        ILogger<EmailVerificationService> logger,
        IMxResolver mxResolver,
        SmtpConnectionLimiter limiter,
        IOptions<VerificationOptions> options)
    {
        _logger = logger;
        _mxResolver = mxResolver;
        _limiter = limiter;
        _options = options.Value;

        _logger.LogInformation(
            "SMTP connection limits: {Limits}; default per MX host: {Default}",
            string.Join(", ", _limiter.Rules.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")),
            _limiter.DefaultLimitPerMxHost);
    }

    public async Task<VerificationResult> VerifyEmailAsync(string email, CancellationToken ct = default)
    {
        VerificationResult? single = null;
        await VerifyAsync([email], (r, _) => { single = r; return ValueTask.CompletedTask; }, ct);
        return single!;
    }

    /// <summary>
    /// Verifies every address and reports each result through <paramref name="onResult"/> as soon as it is known.
    /// <paramref name="onResult"/> is called concurrently from several sessions and must be thread-safe.
    /// </summary>
    public async Task VerifyAsync(
        IReadOnlyCollection<string> emails,
        Func<VerificationResult, CancellationToken, ValueTask> onResult,
        CancellationToken ct)
    {
        var byDomain = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in emails)
        {
            var domain = GetDomain(email);
            if (domain == null)
            {
                await onResult(NewResult(email, errorMessage: "Invalid email format"), ct);
                continue;
            }

            if (!byDomain.TryGetValue(domain, out var list))
                byDomain[domain] = list = new List<string>();
            list.Add(email);
        }

        var lookups = new ConcurrentDictionary<string, MxLookupResult>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            byDomain.Keys,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (domain, token) => lookups[domain] = await _mxResolver.ResolveAsync(domain, token));

        var recipientsByHost = new Dictionary<string, List<Recipient>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, domainEmails) in byDomain)
        {
            var lookup = lookups[domain];
            if (!lookup.HasMx)
            {
                foreach (var email in domainEmails)
                    await onResult(BuildNoMxResult(email, lookup), ct);
                continue;
            }

            var primary = lookup.MxHosts[0];
            if (!recipientsByHost.TryGetValue(primary, out var recipients))
                recipientsByHost[primary] = recipients = new List<Recipient>();
            recipients.AddRange(domainEmails.Select(e => new Recipient(e, domain, lookup.MxHosts)));
        }

        var units = InterleaveByHost(recipientsByHost, Math.Max(1, _options.MaxRecipientsPerSession));

        await Parallel.ForEachAsync(
            units,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _options.MaxParallelSessions), CancellationToken = ct },
            async (unit, token) =>
            {
                foreach (var result in await ProbeUnitAsync(unit, token))
                    await onResult(result, token);
            });
    }

    private async Task<List<VerificationResult>> ProbeUnitAsync(List<Recipient> unit, CancellationToken ct)
    {
        var mxHosts = unit[0].MxHosts;
        var header = new StringBuilder();

        // Acquire the limiter before any SMTP timeout starts so time spent queueing can't expire it.
        var (limiterKey, limit) = _limiter.Resolve(mxHosts[0], unit.Select(r => r.Domain).Distinct(StringComparer.OrdinalIgnoreCase));
        using var lease = limiterKey != null ? await _limiter.AcquireAsync(limiterKey, limit, ct) : null;
        if (limiterKey != null)
            header.AppendLine($"~~ SMTP concurrency limit active for {limiterKey}: max {limit}");

        var results = new Dictionary<string, VerificationResult>(StringComparer.OrdinalIgnoreCase);
        var pending = unit;

        // A session that drops part-way is retried once for the recipients it didn't reach.
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var outcome = await RunSessionAsync(mxHosts, pending, header.ToString(), ct);
            foreach (var result in outcome.Results)
                results[result.EmailAddress] = result;

            if (outcome.Probed == 0)
                break;

            pending = pending.Where(r => outcome.Unfinished.Contains(r.Email)).ToList();
        }

        return unit.Select(r => results[r.Email]).ToList();
    }

    private sealed record SessionOutcome(List<VerificationResult> Results, HashSet<string> Unfinished, int Probed);

    private async Task<SessionOutcome> RunSessionAsync(IReadOnlyList<string> mxHosts, List<Recipient> recipients, string logPrefix, CancellationToken ct)
    {
        var header = new StringBuilder(logPrefix);
        var perRecipientLog = recipients.ToDictionary(r => r.Email, _ => new StringBuilder(), StringComparer.OrdinalIgnoreCase);
        var results = new Dictionary<string, VerificationResult>(StringComparer.OrdinalIgnoreCase);
        var footer = new StringBuilder();

        List<VerificationResult> Finish(string? unfinishedError)
        {
            var list = new List<VerificationResult>(recipients.Count);
            foreach (var recipient in recipients)
            {
                if (!results.TryGetValue(recipient.Email, out var result))
                    result = NewResult(recipient.Email, domainExists: true, hasMx: true, errorMessage: unfinishedError);

                result.SmtpLog = string.Concat(header.ToString(), perRecipientLog[recipient.Email].ToString(), footer.ToString()).TrimEnd();
                list.Add(result);
            }
            return list;
        }

        var (connection, connectError) = await ConnectAsync(mxHosts, header, ct);
        if (connection == null)
            return new SessionOutcome(Finish(connectError), new HashSet<string>(), 0);

        await using (connection)
        {
            var probed = 0;
            try
            {
                var ehlo = await SendLoggedAsync(connection, $"EHLO {_options.EffectiveEhloHost}", header, ct);
                if (ehlo.Code != 250)
                    await SendLoggedAsync(connection, $"HELO {_options.EffectiveEhloHost}", header, ct);

                var mailFrom = await SendLoggedAsync(connection, $"MAIL FROM:<{_options.EffectiveMailFromAddress}>", header, ct);
                if (mailFrom.Code != 250)
                {
                    await QuitAsync(connection, footer, ct);
                    return new SessionOutcome(Finish($"MAIL FROM rejected: {mailFrom.Text}"), new HashSet<string>(), 0);
                }

                var sessionClosedByServer = false;
                foreach (var recipient in recipients)
                {
                    // Sending RCPT TO to the recipient's MX is the core probe of mailbox existence.
                    // lgtm[cs/exposure-of-sensitive-information]
                    var reply = await SendLoggedAsync(connection, $"RCPT TO:<{recipient.Email}>", perRecipientLog[recipient.Email], ct);
                    if (reply.Code == 421)
                    {
                        // The server is closing the session; this and the remaining recipients get a new one.
                        sessionClosedByServer = true;
                        break;
                    }

                    var result = NewResult(recipient.Email, domainExists: true, hasMx: true);
                    result.FirstTestedAt = DateTime.UtcNow;
                    ApplyRcptReply(result, reply);
                    results[recipient.Email] = result;
                    probed++;
                }

                if (!sessionClosedByServer && _options.CatchAllDetection)
                    await DetectCatchAllAsync(connection, recipients, results, perRecipientLog, ct);

                await QuitAsync(connection, footer, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
            {
                var message = ex is TimeoutException ? "SMTP connection timed out" : $"SMTP connection failed: {ex.Message}";
                footer.AppendLine(ex is TimeoutException ? "!! SMTP connection timed out" : $"!! {message}");
                var unfinished = recipients.Where(r => !results.ContainsKey(r.Email)).Select(r => r.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new SessionOutcome(Finish(message), unfinished, probed);
            }

            var remaining = recipients.Where(r => !results.ContainsKey(r.Email)).Select(r => r.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new SessionOutcome(Finish("Temporary SMTP response (421); session closed by remote server"), remaining, probed);
        }
    }

    private async Task<(SmtpConnection? Connection, string? Error)> ConnectAsync(IReadOnlyList<string> mxHosts, StringBuilder log, CancellationToken ct)
    {
        string? error = null;
        foreach (var host in mxHosts.Take(Math.Max(1, _options.MaxMxHostsToTry)))
        {
            SmtpConnection? connection = null;
            try
            {
                log.AppendLine($"~~ Connecting to {host}:{_options.Port}");
                connection = await SmtpConnection.OpenAsync(host, _options.Port, _options.CommandTimeoutMs, ct);
                var banner = await connection.ReadReplyAsync(ct);
                log.AppendLine($"<< {banner.Text}");

                if (banner.Code == 220)
                    return (connection, null);

                error = $"Unexpected SMTP banner: {banner.Text}";
            }
            catch (TimeoutException)
            {
                log.AppendLine("!! SMTP connection timed out");
                error = "SMTP connection timed out";
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                log.AppendLine($"!! SMTP connection failed: {ex.Message}");
                error = $"SMTP connection failed: {ex.Message}";
            }

            if (connection != null)
                await connection.DisposeAsync();
        }

        return (null, error);
    }

    /// <summary>
    /// Probes a random address at each domain that accepted a real recipient. If the random address is also
    /// accepted, the domain accepts everything and "mailbox exists" carries no signal.
    /// </summary>
    private async Task DetectCatchAllAsync(
        SmtpConnection connection,
        List<Recipient> recipients,
        Dictionary<string, VerificationResult> results,
        Dictionary<string, StringBuilder> logs,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var domainGroup in recipients.GroupBy(r => r.Domain, StringComparer.OrdinalIgnoreCase))
        {
            var accepted = domainGroup
                .Where(r => results.TryGetValue(r.Email, out var res) && res.MailboxExists)
                .ToList();
            if (accepted.Count == 0)
                continue;

            bool? isCatchAll = null;
            string note;
            if (_catchAllCache.TryGetValue(domainGroup.Key, out var cached) && cached.ExpiresAt > now)
            {
                isCatchAll = cached.IsCatchAll;
                note = $"~~ Catch-all (cached): {(isCatchAll.Value ? "yes" : "no")}";
            }
            else
            {
                var probeAddress = $"mv-probe-{Guid.NewGuid():N}"[..24] + "@" + domainGroup.Key;
                var reply = await connection.SendAsync($"RCPT TO:<{probeAddress}>", ct);
                isCatchAll = reply.Code switch
                {
                    250 or 251 => true,
                    >= 500 and <= 599 => false,
                    _ => null
                };
                note = $"~~ Catch-all probe RCPT TO:<{probeAddress}> -> {reply.Text}";
                if (isCatchAll.HasValue)
                    _catchAllCache[domainGroup.Key] = (isCatchAll.Value, now + CatchAllCacheTtl);
            }

            foreach (var recipient in accepted)
            {
                logs[recipient.Email].AppendLine(note);
                results[recipient.Email].IsCatchAll = isCatchAll == true;
            }
        }
    }

    internal static void ApplyRcptReply(VerificationResult result, SmtpReply reply)
    {
        switch (reply.Code)
        {
            case 250 or 251:
                result.MailboxExists = true;
                break;
            case >= 500 and <= 599:
                result.MailboxExists = false;
                break;
            case >= 400 and <= 499:
                // Temporary responses (including greylisting codes 421/450/451) are retryable, not hard failures.
                result.MailboxExists = false;
                result.ErrorMessage = $"Temporary SMTP response ({reply.Code}); mailbox status inconclusive: {reply.Text}";
                break;
            default:
                result.MailboxExists = false;
                result.ErrorMessage = $"Inconclusive SMTP response: {reply.Text}";
                break;
        }
    }

    private static async Task<SmtpReply> SendLoggedAsync(SmtpConnection connection, string command, StringBuilder log, CancellationToken ct)
    {
        log.AppendLine($">> {command}");
        var reply = await connection.SendAsync(command, ct);
        log.AppendLine($"<< {reply.Text}");
        return reply;
    }

    private static async Task QuitAsync(SmtpConnection connection, StringBuilder log, CancellationToken ct)
    {
        try
        {
            await SendLoggedAsync(connection, "QUIT", log, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            log.AppendLine("!! QUIT failed or connection already closed");
        }
    }

    private static VerificationResult BuildNoMxResult(string email, MxLookupResult lookup)
    {
        if (lookup.Error != null)
            return NewResult(email, errorMessage: lookup.Error);

        if (!lookup.DomainExists)
            return NewResult(email, errorMessage: "Domain does not exist and has no MX records");

        var result = NewResult(email, domainExists: true);
        result.SmtpLog = "No MX host available for SMTP check";
        return result;
    }

    private static VerificationResult NewResult(string email, bool domainExists = false, bool hasMx = false, string? errorMessage = null) => new()
    {
        EmailAddress = email,
        DomainExists = domainExists,
        HasMxRecords = hasMx,
        ErrorMessage = errorMessage,
        VerifiedAt = DateTime.UtcNow
    };

    internal static string? GetDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex <= 0 || atIndex == email.Length - 1)
            return null;

        var domain = email[(atIndex + 1)..].Trim().TrimEnd('.');
        return domain.Length == 0 ? null : domain;
    }

    /// <summary>Chunks each host's recipients into sessions and orders them round-robin across hosts,
    /// so one throttled host doesn't occupy every worker while others sit idle.</summary>
    private static List<List<Recipient>> InterleaveByHost(Dictionary<string, List<Recipient>> byHost, int chunkSize)
    {
        var queues = byHost.Values
            .Select(list => new Queue<List<Recipient>>(list.Chunk(chunkSize).Select(c => c.ToList())))
            .ToList();

        var ordered = new List<List<Recipient>>();
        while (queues.Count > 0)
        {
            foreach (var queue in queues)
                ordered.Add(queue.Dequeue());
            queues.RemoveAll(q => q.Count == 0);
        }

        return ordered;
    }

    private sealed record Recipient(string Email, string Domain, IReadOnlyList<string> MxHosts);
}
