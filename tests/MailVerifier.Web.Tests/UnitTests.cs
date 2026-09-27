using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MailVerifier.Web.Pages.Jobs;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;
using MailVerifier.Web.Services.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Tests;

public class SmtpConnectionLimiterTests
{
    private static SmtpConnectionLimiter Create(Dictionary<string, string?>? config = null, int defaultPerHost = 5) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build(),
            Options.Create(new VerificationOptions { DefaultConnectionLimitPerMxHost = defaultPerHost }));

    [Theory]
    [InlineData("live.com", "live.com", true)]
    [InlineData("mx1.live.com", "live.com", true)]
    [InlineData("alive.com", "live.com", false)]
    [InlineData("olive.com", "live.com", false)]
    [InlineData("LIVE.COM.", "live.com", true)]
    public void MatchesDomain_requires_a_label_boundary(string host, string pattern, bool expected) =>
        Assert.Equal(expected, SmtpConnectionLimiter.MatchesDomain(host, pattern));

    [Fact]
    public void Microsoft_365_tenant_mx_uses_the_most_specific_rule()
    {
        var (key, limit) = Create().Resolve("contoso-com.mail.protection.outlook.com", ["contoso.com"]);
        Assert.Equal("mail.protection.outlook.com", key);
        Assert.Equal(4, limit);
    }

    [Fact]
    public void Consumer_microsoft_mx_matches_outlook_rule()
    {
        var (key, limit) = Create().Resolve("hotmail-com.olc.protection.outlook.com", ["hotmail.com"]);
        Assert.Equal("outlook.com", key);
        Assert.Equal(2, limit);
    }

    [Fact]
    public void Recipient_domain_rule_applies_when_mx_has_no_rule()
    {
        var limiter = Create(new() { ["Smtp:ConnectionLimits:example.org"] = "1" });
        Assert.Equal(("example.org", 1), limiter.Resolve("mx.hosting.test", ["example.org"]));
    }

    [Fact]
    public void Unmatched_mx_gets_default_per_host_limit_or_none()
    {
        Assert.Equal(("mx:aspmx.l.google.com", 5), Create().Resolve("ASPMX.L.GOOGLE.COM", ["gmail.com"]));
        Assert.Equal(((string?)null, 0), Create(defaultPerHost: 0).Resolve("aspmx.l.google.com", ["gmail.com"]));
    }

    [Fact]
    public async Task Limiter_caps_concurrent_leases()
    {
        var limiter = Create();
        var first = await limiter.AcquireAsync("k", 1, CancellationToken.None);
        var second = limiter.AcquireAsync("k", 1, CancellationToken.None);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
    }
}

public class EmailAddressDeduplicatorTests
{
    [Fact]
    public void Deduplicate_normalizes_case_and_whitespace()
    {
        var result = EmailAddressDeduplicator.Deduplicate([" John@Example.com ", "john@example.com", "", "  ", "OTHER@x.io"]);
        Assert.Equal(["john@example.com", "other@x.io"], result);
    }
}

public class CsvSafeTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "'=HYPERLINK(\"x\")")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("user@example.com", "user@example.com")]
    [InlineData(null, null)]
    public void Formula_prefixes_are_neutralised(string? input, string? expected) =>
        Assert.Equal(expected, JobDetailsModel.CsvSafe(input));
}

public class RcptReplyTests
{
    [Theory]
    [InlineData(250, true, false)]
    [InlineData(251, true, false)]
    [InlineData(550, false, false)]
    [InlineData(450, false, true)]
    public void Classifies_reply_codes(int code, bool exists, bool retryable)
    {
        var result = new Models.VerificationResult { EmailAddress = "a@b.c", DomainExists = true, HasMxRecords = true };
        EmailVerificationService.ApplyRcptReply(result, new SmtpReply(code, $"{code} text"));
        Assert.Equal(exists, result.MailboxExists);
        Assert.Equal(retryable, result.IsRetryable);
    }
}

public class UserAccessTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void Admin_from_role_claim_list() =>
        Assert.True(UserAccess.IsAdmin(Principal(new Claim("roles", "user, Admin"))));

    [Fact]
    public void Admin_from_keycloak_resource_access() =>
        Assert.True(UserAccess.IsAdmin(Principal(new Claim("resource_access", """{"mail-verifier":{"roles":["admin"]}}"""))));

    [Fact]
    public void Non_admin() =>
        Assert.False(UserAccess.IsAdmin(Principal(new Claim("realm_access", """{"roles":["user"]}"""))));

    [Fact]
    public void Roles_from_jwt_are_filtered_to_client_and_account()
    {
        var payload = JsonSerializer.Serialize(new
        {
            realm_access = new { roles = new[] { "realm-role" } },
            resource_access = new Dictionary<string, object>
            {
                ["mail-verifier"] = new { roles = new[] { "admin" } },
                ["account"] = new { roles = new[] { "manage-account" } },
                ["other-app"] = new { roles = new[] { "other" } }
            }
        });
        var jwt = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

        var roles = KeycloakRoles.FromJwt(jwt, "mail-verifier").ToList();

        Assert.Equal(["realm-role", "admin", "manage-account"], roles);
    }
}
