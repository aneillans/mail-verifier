using System.Collections.Concurrent;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DnsClient;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Services;

public class EmailVerificationService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SmtpConnectionSemaphores = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> UnmatchedLimitDomainsLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> DefaultRecipientDomainConnectionLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hotmail.com"] = 2,
        ["outlook.com"] = 2,
        ["live.com"] = 2,
        ["msn.com"] = 2
    };

    private readonly ILogger<EmailVerificationService> _logger;
    private readonly LookupClient _dnsClient;
    private readonly string _ehloHost;
    private readonly string _mailFromAddress;
    private readonly Dictionary<string, int> _recipientDomainConnectionLimits;
    private const int SmtpTimeoutMs = 10000;

    // Per-instance MX cache: avoids redundant DNS for repeated domains within a single job run.
    private readonly ConcurrentDictionary<string, string?> _mxCache = new(StringComparer.OrdinalIgnoreCase);

    public EmailVerificationService(ILogger<EmailVerificationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _dnsClient = new LookupClient();
        // Prefer environment variable first to ensure it's used in containerized deployment
        var ehloHost = Environment.GetEnvironmentVariable("Smtp__EhloHost")
            ?? configuration["Smtp:EhloHost"];
        _ehloHost = ResolveEhloHost(ehloHost);
        
        // Prefer environment variable first, then configuration, then default to verify@{ehloHost}
        var mailFromAddr = Environment.GetEnvironmentVariable("Smtp__MailFromAddress")
            ?? configuration["Smtp:MailFromAddress"]
            ?? $"verify@{_ehloHost}";
        _mailFromAddress = mailFromAddr.Trim();

        _recipientDomainConnectionLimits = BuildRecipientDomainConnectionLimits(configuration);

        _logger.LogInformation(
            "Effective SMTP connection limits: {Limits}",
            string.Join(", ", _recipientDomainConnectionLimits
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}={x.Value}")));
    }

    public async Task<VerificationResult> VerifyEmailAsync(string email)
    {
        var result = new VerificationResult
        {
            EmailAddress = email,
            VerifiedAt = DateTime.UtcNow
        };

        var smtpLog = new StringBuilder();

        try
        {
            var atIndex = email.IndexOf('@');
            if (atIndex < 0 || atIndex == email.Length - 1)
            {
                result.ErrorMessage = "Invalid email format";
                return result;
            }

            var domain = email[(atIndex + 1)..];
            string? mxHost;

            if (_mxCache.TryGetValue(domain, out var cachedMxHost))
            {
                // Domain already resolved in this job run — skip DNS entirely
                mxHost = cachedMxHost;
                result.DomainExists = mxHost != null;
                result.HasMxRecords = mxHost != null;
            }
            else
            {
                // Run A/CNAME and MX queries in parallel
                var aTask = LookupDomainExistsAsync(domain);
                var mxTask = LookupMxHostAsync(domain);
                await Task.WhenAll(aTask, mxTask);

                result.DomainExists = aTask.Result;
                mxHost = mxTask.Result;

                if (mxHost != null)
                {
                    result.HasMxRecords = true;
                    result.DomainExists = true;
                }

                _mxCache[domain] = mxHost;
            }

            if (!result.DomainExists && mxHost == null)
            {
                result.ErrorMessage = "Domain does not exist and has no MX records";
                result.SmtpLog = smtpLog.ToString();
                return result;
            }

            if (mxHost != null)
            {
                await PerformSmtpCheckAsync(email, mxHost, result, smtpLog);
            }
            else
            {
                smtpLog.AppendLine("No MX host available for SMTP check");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error verifying email {Email}", email);
            result.ErrorMessage = $"Unexpected error: {ex.Message}";
        }

        result.SmtpLog = smtpLog.ToString();
        return result;
    }

    private async Task<bool> LookupDomainExistsAsync(string domain)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(domain);
            return entry.AddressList.Length > 0 || entry.HostName.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> LookupMxHostAsync(string domain)
    {
        try
        {
            var response = await _dnsClient.QueryAsync(domain, QueryType.MX);
            var mx = response.Answers.MxRecords().OrderBy(r => r.Preference).FirstOrDefault();
            return mx?.Exchange.Value.TrimEnd('.');
        }
        catch
        {
            return null;
        }
    }

    private async Task PerformSmtpCheckAsync(string email, string mxHost, VerificationResult result, StringBuilder smtpLog)
    {
        TcpClient? tcp = null;
        SemaphoreSlim? domainLimiter = null;
        var limiterAcquired = false;
        try
        {
            // Acquire semaphore OUTSIDE the SMTP timeout so queueing doesn't expire the timeout.
            var maxConnections = GetSmtpConnectionLimit(email, out var limiterKey);
            if (maxConnections > 0 && limiterKey != null)
            {
                domainLimiter = SmtpConnectionSemaphores.GetOrAdd(
                    limiterKey,
                    _ => new SemaphoreSlim(maxConnections, maxConnections));

                var waitTimer = Stopwatch.StartNew();
                await domainLimiter.WaitAsync();
                waitTimer.Stop();
                limiterAcquired = true;
                smtpLog.AppendLine($"~~ SMTP concurrency limit active for {limiterKey}: max {maxConnections}");
                _logger.LogDebug(
                    "SMTP limiter acquired for {LimiterKey} (recipient {Email}, mx {MxHost}, max {MaxConnections}, waitMs {WaitMs})",
                    limiterKey,
                    email,
                    mxHost,
                    maxConnections,
                    waitTimer.ElapsedMilliseconds);
            }
            else
            {
                var recipientDomain = GetRecipientDomain(email);
                if (!string.IsNullOrWhiteSpace(recipientDomain) && UnmatchedLimitDomainsLogged.TryAdd(recipientDomain, 0))
                {
                    _logger.LogWarning(
                        "No SMTP connection limit matched for recipient domain {RecipientDomain} (mx {MxHost}). Configure Smtp:ConnectionLimits for this domain to enforce throttling.",
                        recipientDomain,
                        mxHost);
                }
            }

            // NOW start the SMTP timeout (connect, EHLO, MAIL FROM, RCPT TO, QUIT).
            using var cts = new CancellationTokenSource(SmtpTimeoutMs);

            tcp = new TcpClient();

            await tcp.ConnectAsync(mxHost, 25, cts.Token);

            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

            // Read banner
            var banner = await ReadResponseAsync(reader, cts.Token);
            smtpLog.AppendLine($"<< {banner}");

            if (!banner.StartsWith("220"))
            {
                result.ErrorMessage = $"Unexpected SMTP banner: {banner}";
                return;
            }

            // Send EHLO
            var ehloCommand = $"EHLO {_ehloHost}";
            await writer.WriteLineAsync(ehloCommand.AsMemory(), cts.Token);
            smtpLog.AppendLine($">> {ehloCommand}");
            var ehloResponse = await ReadResponseAsync(reader, cts.Token);
            smtpLog.AppendLine($"<< {ehloResponse}");

            // Send MAIL FROM
            await writer.WriteLineAsync($"MAIL FROM:<{_mailFromAddress}>".AsMemory(), cts.Token);
            smtpLog.AppendLine($">> MAIL FROM:<{_mailFromAddress}>");
            var mailFromResponse = await ReadResponseAsync(reader, cts.Token);
            smtpLog.AppendLine($"<< {mailFromResponse}");

            if (!mailFromResponse.StartsWith("250"))
            {
                result.ErrorMessage = $"MAIL FROM rejected: {mailFromResponse}";
                await SendQuitAsync(writer, reader, smtpLog, cts.Token);
                return;
            }

            // Mark the original domain/MX state before SMTP for retesting
            if (result.DomainExists) result.FirstTestedAt = DateTime.UtcNow;

            // Send RCPT TO — intentionally sends the email address to the remote MX server;
            // this is the core probe of mailbox existence and is expected behaviour.
            // lgtm[cs/exposure-of-sensitive-information]
            await writer.WriteLineAsync($"RCPT TO:<{email}>".AsMemory(), cts.Token);
            smtpLog.AppendLine($">> RCPT TO:<{email}>");
            var rcptResponse = await ReadResponseAsync(reader, cts.Token);
            smtpLog.AppendLine($"<< {rcptResponse}");

            if (!TryGetSmtpStatusCode(rcptResponse, out var rcptStatusCode))
            {
                result.MailboxExists = false;
                result.ErrorMessage = $"Inconclusive SMTP response: {rcptResponse}";
            }
            else if (rcptStatusCode is 250 or 251)
            {
                result.MailboxExists = true;
            }
            else if (rcptStatusCode >= 500 && rcptStatusCode <= 599)
            {
                result.MailboxExists = false;
            }
            else if (rcptStatusCode >= 400 && rcptStatusCode <= 499)
            {
                // Temporary responses (including common greylisting codes 421/450/451)
                // are treated as retryable/inconclusive rather than hard mailbox failures.
                result.MailboxExists = false;
                result.ErrorMessage = $"Temporary SMTP response ({rcptStatusCode}); mailbox status inconclusive: {rcptResponse}";
            }
            else
            {
                result.MailboxExists = false;
                result.ErrorMessage = $"Inconclusive SMTP response: {rcptResponse}";
            }

            // Send QUIT
            await SendQuitAsync(writer, reader, smtpLog, cts.Token);
        }
        catch (OperationCanceledException)
        {
            smtpLog.AppendLine("!! SMTP connection timed out");
            result.ErrorMessage = "SMTP connection timed out";
        }
        catch (SocketException ex)
        {
            smtpLog.AppendLine($"!! SMTP connection failed: {ex.Message}");
            result.ErrorMessage = $"SMTP connection failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            smtpLog.AppendLine($"!! SMTP error: {ex.Message}");
            result.ErrorMessage = $"SMTP error: {ex.Message}";
        }
        finally
        {
            if (limiterAcquired)
            {
                domainLimiter!.Release();
            }

            tcp?.Close();
        }
    }

    private Dictionary<string, int> BuildRecipientDomainConnectionLimits(IConfiguration configuration)
    {
        var limits = new Dictionary<string, int>(DefaultRecipientDomainConnectionLimits, StringComparer.OrdinalIgnoreCase);

        var configLimits = configuration.GetSection("Smtp:ConnectionLimits");
        foreach (var child in configLimits.GetChildren())
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

            var rawDomain = key[envPrefix.Length..];
            var domain = NormalizeDomainKey(rawDomain);
            if (domain == null)
                continue;

            if (entry.Value is string rawValue && int.TryParse(rawValue, out var maxConnections) && maxConnections > 0)
            {
                limits[domain] = maxConnections;
            }
        }

        return limits;
    }

    private int GetSmtpConnectionLimit(string email, out string? limiterKey)
    {
        limiterKey = null;

        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return 0;

        var recipientDomain = email[(atIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(recipientDomain))
            return 0;

        // Sort by domain length descending to match longest/most-specific pattern first.
        // e.g., "hotmail.com" before "com", "outlook.live.com" before "live.com".
        var sortedDomains = _recipientDomainConnectionLimits
            .OrderByDescending(x => x.Key.Length)
            .ToList();

        foreach (var (domain, maxConnections) in sortedDomains)
        {
            if (recipientDomain.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                limiterKey = domain;
                return maxConnections;
            }
        }

        return 0;
    }

    private static string? NormalizeDomainKey(string? value)
    {
        var domain = value?.Trim().TrimStart('.').ToLowerInvariant();
        return string.IsNullOrWhiteSpace(domain) ? null : domain;
    }

    private static string? GetRecipientDomain(string email)
    {
        var atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
            return null;

        var domain = email[(atIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(domain) ? null : domain;
    }

    private static async Task SendQuitAsync(StreamWriter writer, StreamReader reader, StringBuilder smtpLog, CancellationToken ct)
    {
        try
        {
            await writer.WriteLineAsync("QUIT".AsMemory(), ct);
            smtpLog.AppendLine(">> QUIT");
            var quitResponse = await ReadResponseAsync(reader, ct);
            smtpLog.AppendLine($"<< {quitResponse}");
        }
        catch
        {
            smtpLog.AppendLine("!! QUIT failed or connection already closed");
        }
    }

    private static async Task<string> ReadResponseAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            sb.AppendLine(line);
            // Multi-line responses have a hyphen after the code, e.g. "250-..."
            // Single/last line has a space: "250 ..."
            if (line.Length >= 4 && line[3] == ' ')
                break;
            if (line.Length < 4)
                break;
        }
        return sb.ToString().TrimEnd();
    }

    private static string ResolveEhloHost(string? configuredValue)
    {
        var host = configuredValue?.Trim();
        return string.IsNullOrWhiteSpace(host) ? "mailverifier.local" : host;
    }

    private static bool TryGetSmtpStatusCode(string response, out int code)
    {
        code = 0;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        var firstLine = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return firstLine is not null &&
               firstLine.Length >= 3 &&
               int.TryParse(firstLine[..3], out code);
    }
}
