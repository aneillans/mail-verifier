using System.Collections.Concurrent;
using MailVerifier.Web.Models;
using MailVerifier.Web.Services;
using MailVerifier.Web.Services.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Tests;

public class EmailVerificationServiceTests
{
    private sealed class FakeMxResolver(Dictionary<string, MxLookupResult> lookups) : IMxResolver
    {
        public int Calls;

        public Task<MxLookupResult> ResolveAsync(string domain, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(lookups.TryGetValue(domain, out var r) ? r : new MxLookupResult(false, []));
        }
    }

    private static EmailVerificationService CreateService(FakeSmtpServer server, IMxResolver resolver, Action<VerificationOptions>? configure = null)
    {
        var options = new VerificationOptions { Port = server.Port, CommandTimeoutMs = 3000, MaxParallelSessions = 4 };
        configure?.Invoke(options);
        var wrapped = Options.Create(options);
        var limiter = new SmtpConnectionLimiter(new ConfigurationBuilder().Build(), wrapped);
        return new EmailVerificationService(NullLogger<EmailVerificationService>.Instance, resolver, limiter, wrapped);
    }

    private static async Task<Dictionary<string, VerificationResult>> VerifyAllAsync(EmailVerificationService service, params string[] emails)
    {
        var results = new ConcurrentDictionary<string, VerificationResult>();
        await service.VerifyAsync(emails, (r, _) => { results[r.EmailAddress] = r; return ValueTask.CompletedTask; }, CancellationToken.None);
        return new Dictionary<string, VerificationResult>(results);
    }

    private static FakeMxResolver LoopbackMx(params string[] domains) =>
        new(domains.ToDictionary(d => d, _ => new MxLookupResult(true, ["127.0.0.1"])));

    [Fact]
    public async Task Recipients_sharing_an_mx_host_use_one_session()
    {
        await using var server = new FakeSmtpServer
        {
            RcptReply = a => a.StartsWith("good") ? "250 OK" : "550 5.1.1 No such user"
        };
        var service = CreateService(server, LoopbackMx("example.com"));

        var results = await VerifyAllAsync(service, "good1@example.com", "good2@example.com", "bad@example.com");

        Assert.Equal(1, server.Sessions);
        Assert.True(results["good1@example.com"].IsVerified);
        Assert.True(results["good2@example.com"].IsVerified);
        Assert.True(results["bad@example.com"].IsInvalidMailbox);
        Assert.False(results["good1@example.com"].IsCatchAll);
        Assert.Contains("RCPT TO:<good1@example.com>", results["good1@example.com"].SmtpLog);
        Assert.DoesNotContain("RCPT TO:<good2@example.com>", results["good1@example.com"].SmtpLog);
    }

    [Fact]
    public async Task Sessions_are_split_by_max_recipients()
    {
        await using var server = new FakeSmtpServer { RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : "250 OK" };
        var service = CreateService(server, LoopbackMx("example.com"), o => o.MaxRecipientsPerSession = 2);

        var results = await VerifyAllAsync(service, "a@example.com", "b@example.com", "c@example.com", "d@example.com", "e@example.com");

        Assert.Equal(3, server.Sessions);
        Assert.All(results.Values, r => Assert.True(r.IsVerified));
    }

    [Fact]
    public async Task Catch_all_domain_is_flagged_at_risk()
    {
        await using var server = new FakeSmtpServer { RcptReply = _ => "250 OK" };
        var service = CreateService(server, LoopbackMx("catchall.test"));

        var results = await VerifyAllAsync(service, "anyone@catchall.test");

        var result = results["anyone@catchall.test"];
        Assert.True(result.MailboxExists);
        Assert.True(result.IsCatchAll);
        Assert.True(result.IsAtRisk);
        Assert.Contains(server.RcptCommands, a => a.StartsWith("mv-probe-"));
    }

    [Fact]
    public async Task Catch_all_result_is_cached_per_domain()
    {
        await using var server = new FakeSmtpServer { RcptReply = _ => "250 OK" };
        var service = CreateService(server, LoopbackMx("catchall.test"));

        await VerifyAllAsync(service, "one@catchall.test");
        var second = await VerifyAllAsync(service, "two@catchall.test");

        Assert.True(second["two@catchall.test"].IsCatchAll);
        Assert.Single(server.RcptCommands, a => a.StartsWith("mv-probe-"));
    }

    [Fact]
    public async Task Temporary_failure_is_retryable()
    {
        await using var server = new FakeSmtpServer { RcptReply = _ => "451 4.7.1 Greylisted" };
        var service = CreateService(server, LoopbackMx("example.com"));

        var results = await VerifyAllAsync(service, "user@example.com");

        Assert.True(results["user@example.com"].IsRetryable);
        Assert.False(results["user@example.com"].IsInvalidMailbox);
    }

    [Fact]
    public async Task Session_closed_with_421_is_retried_for_remaining_recipients()
    {
        await using var server = new FakeSmtpServer { CloseAfterRcpts = 2, RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : "250 OK" };
        var service = CreateService(server, LoopbackMx("example.com"));

        var results = await VerifyAllAsync(service, "a@example.com", "b@example.com", "c@example.com");

        Assert.Equal(2, server.Sessions);
        Assert.All(results.Values, r => Assert.True(r.IsVerified, r.SmtpLog));
    }

    [Fact]
    public async Task Falls_back_to_next_mx_host_when_first_is_unreachable()
    {
        await using var server = new FakeSmtpServer { RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : "250 OK" };
        // Nothing listens on 127.0.0.2 for this port, so the connection is refused.
        var resolver = new FakeMxResolver(new() { ["example.com"] = new MxLookupResult(true, ["127.0.0.2", "127.0.0.1"]) });
        var service = CreateService(server, resolver);

        var results = await VerifyAllAsync(service, "user@example.com");

        Assert.True(results["user@example.com"].IsVerified, results["user@example.com"].SmtpLog);
        Assert.Contains("127.0.0.2", results["user@example.com"].SmtpLog);
    }

    [Fact]
    public async Task Domains_without_mx_are_reported_without_smtp()
    {
        await using var server = new FakeSmtpServer();
        var resolver = new FakeMxResolver(new()
        {
            ["nomx.test"] = new MxLookupResult(true, []),
            ["dnsfail.test"] = new MxLookupResult(false, [], "DNS lookup failed: SERVFAIL")
        });
        var service = CreateService(server, resolver);

        var results = await VerifyAllAsync(service, "a@missing.test", "b@nomx.test", "c@dnsfail.test", "not-an-email");

        Assert.Equal(0, server.Sessions);
        Assert.Equal("Domain does not exist and has no MX records", results["a@missing.test"].ErrorMessage);
        Assert.True(results["b@nomx.test"].DomainExists);
        Assert.False(results["b@nomx.test"].HasMxRecords);
        Assert.Equal("DNS lookup failed: SERVFAIL", results["c@dnsfail.test"].ErrorMessage);
        Assert.Equal("Invalid email format", results["not-an-email"].ErrorMessage);
    }

    [Fact]
    public async Task Each_domain_is_resolved_once_per_call()
    {
        await using var server = new FakeSmtpServer { RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : "250 OK" };
        var resolver = LoopbackMx("example.com");
        var service = CreateService(server, resolver);

        await VerifyAllAsync(service, "a@example.com", "b@example.com", "c@example.com");

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_recording_timeouts()
    {
        await using var server = new FakeSmtpServer();
        var service = CreateService(server, LoopbackMx("example.com"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.VerifyAsync(["a@example.com"], (_, _) => ValueTask.CompletedTask, cts.Token));
    }
}
