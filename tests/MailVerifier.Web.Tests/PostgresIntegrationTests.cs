using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Services.Verification;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailVerifier.Web.Tests;

/// <summary>Runs only when MAILVERIFIER_TEST_PG holds a connection string to a disposable Postgres database.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILVERIFIER_TEST_PG")))
            Skip = "Set MAILVERIFIER_TEST_PG to run Postgres integration tests";
    }
}

public class PostgresIntegrationTests
{
    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim("sub", "user-1"), new Claim("name", "Test User")], "Test");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }

    private sealed class LoopbackResolver : IMxResolver
    {
        public Task<MxLookupResult> ResolveAsync(string domain, CancellationToken ct) =>
            Task.FromResult(domain == "nomx.test" ? new MxLookupResult(false, []) : new MxLookupResult(true, ["127.0.0.1"]));
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeSmtpServer smtp) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:DefaultConnection", Environment.GetEnvironmentVariable("MAILVERIFIER_TEST_PG"));
            b.UseSetting("Smtp:Port", smtp.Port.ToString());
            b.UseSetting("Smtp:CommandTimeoutMs", "3000");
            b.UseSetting("OpenIdConnect:Authority", "https://idp.invalid/realms/test");
            b.UseSetting("OpenIdConnect:ClientId", "mail-verifier");
            b.ConfigureTestServices(services =>
            {
                services.AddSingleton<IMxResolver, LoopbackResolver>();
                services.AddAuthentication(o =>
                {
                    o.DefaultScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
            });
        });

    private static async Task<(HttpClient Client, string Token)> ClientWithAntiforgeryAsync(WebApplicationFactory<Program> factory, string page)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pageResponse = await client.GetAsync(page);
        var html = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(pageResponse.IsSuccessStatusCode, $"{page}: {(int)pageResponse.StatusCode} {html[..Math.Min(html.Length, 3000)]}");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(token), "antiforgery token not found on " + page);
        return (client, token);
    }

    private static async Task<int> UploadAsync(HttpClient client, string token, string csv)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent("integration"), "jobName" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "csvFile", "emails.csv" }
        };
        var response = await client.PostAsync("/Upload", form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, int jobId, params string[] statuses)
    {
        for (var i = 0; i < 100; i++)
        {
            var progress = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/progress");
            if (statuses.Contains(progress.GetProperty("status").GetString()))
                return progress;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Job {jobId} never reached {string.Join("/", statuses)}");
    }

    [PostgresFact]
    public async Task Upload_verify_progress_rerun_and_stop()
    {
        await using var smtp = new FakeSmtpServer
        {
            RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : a.StartsWith("grey") ? "451 greylisted" : a.StartsWith("bad") ? "550 unknown" : "250 OK"
        };
        await using var factory = CreateFactory(smtp);
        var (client, token) = await ClientWithAntiforgeryAsync(factory, "/Upload");

        // Mixed case + duplicate collapse to one lower-cased address.
        var jobId = await UploadAsync(client, token, "email\nGood@Example.com\ngood@example.com\nbad@example.com\ngrey@example.com\nx@nomx.test\n");

        var progress = await WaitForStatusAsync(client, jobId, JobStatus.Completed);
        Assert.Equal(4, progress.GetProperty("totalEmails").GetInt32());
        Assert.Equal(4, progress.GetProperty("processedEmails").GetInt32());
        Assert.Equal(1, progress.GetProperty("validCount").GetInt32());
        var results = progress.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(results, r => r.GetProperty("emailAddress").GetString() == "good@example.com");
        Assert.All(results, r => Assert.False(r.TryGetProperty("smtpLog", out _)));

        // Delta polling: nothing changed after the cursor (beyond the overlap window) once the job is done.
        var cursor = progress.GetProperty("cursor").GetInt64();
        var later = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{jobId}/progress?since={cursor + 60_000}");
        Assert.Empty(later.GetProperty("results").EnumerateArray());

        // Logs are fetched on demand.
        var goodId = results.First(r => r.GetProperty("emailAddress").GetString() == "good@example.com").GetProperty("id").GetInt32();
        var log = await client.GetStringAsync($"/api/jobs/{jobId}/results/{goodId}/log");
        Assert.Contains("RCPT TO:<good@example.com>", log);

        // Rerun re-verifies only failed results (grey@ and nomx), not the rest.
        smtp.RcptReply = a => a.StartsWith("mv-probe") ? "550 no" : a.StartsWith("bad") ? "550 unknown" : "250 OK";
        while (smtp.RcptCommands.TryDequeue(out _)) { }
        var (detailsClient, detailsToken) = await ClientWithAntiforgeryAsync(factory, $"/Jobs/Details/{jobId}");
        var rerun = await detailsClient.PostAsync($"/Jobs/Details/{jobId}?handler=RerunTimeouts",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = detailsToken }));
        Assert.Equal(HttpStatusCode.Redirect, rerun.StatusCode);

        progress = await WaitForStatusAsync(client, jobId, JobStatus.Completed);
        Assert.Equal(2, progress.GetProperty("validCount").GetInt32());
        Assert.Equal(["grey@example.com"], smtp.RcptCommands.Where(a => !a.StartsWith("mv-probe")).ToArray());

        // CSV export
        var csv = await detailsClient.PostAsync($"/Jobs/Details/{jobId}?handler=DownloadCsv",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = detailsToken }));
        var csvText = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("Email,DomainExists", csvText);
        Assert.Contains("grey@example.com,True,True,True,True", csvText);

        // Stop: a slow server keeps the job running; Stop must end it as Stopped, not Completed.
        smtp.RcptReply = a => { Thread.Sleep(300); return "250 OK"; };
        var bigCsv = "email\n" + string.Join("\n", Enumerable.Range(0, 200).Select(i => $"user{i}@slow.test"));
        var slowJob = await UploadAsync(client, token, bigCsv);
        await WaitForStatusAsync(client, slowJob, JobStatus.Processing);
        var (stopClient, stopToken) = await ClientWithAntiforgeryAsync(factory, $"/Jobs/Details/{slowJob}");
        await stopClient.PostAsync($"/Jobs/Details/{slowJob}?handler=StopJob",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = stopToken }));

        await Task.Delay(TimeSpan.FromSeconds(5));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stopped = await db.VerificationJobs.AsNoTracking().FirstAsync(j => j.Id == slowJob);
        Assert.Equal(JobStatus.Stopped, stopped.Status);
        var persisted = await db.VerificationResults.CountAsync(r => r.JobId == slowJob);
        Assert.True(persisted < 200, $"expected stop to halt processing, but {persisted} results were stored");

        // Pages render; admin pages are closed to non-admins by the Admin policy.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Jobs/SqlScript/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/EmailSearch?q=good@example.com")).StatusCode);

        // Health endpoint is anonymous and healthy.
        var health = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
