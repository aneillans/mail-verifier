using System.Net.Http;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MailVerifier.Web.Data;
using MailVerifier.Web.Services;

namespace MailVerifier.Web.Health;

public static class HealthEndpoints
{
    public const string LivePath = "/healthz/live";
    public const string ReadyPath = "/healthz";
    private const string ReadyTag = "ready";

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: [ReadyTag])
            .AddCheck<VerificationQueueHealthCheck>("verification-queue", tags: [ReadyTag]);
        return services;
    }

    /// <summary>
    /// /healthz/live: the process is serving requests. /healthz: the database is reachable and the
    /// verification queue is running. Both are anonymous and return only the aggregate status.
    /// </summary>
    public static IEndpointRouteBuilder MapAppHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return app;
    }

    /// <summary>
    /// `dotnet MailVerifier.Web.dll --healthcheck` probes the running instance. The chiseled runtime image has
    /// no curl or wget, so the container HEALTHCHECK uses the app itself. Exit code 0 means healthy.
    /// </summary>
    public static async Task<int> RunProbeAsync()
    {
        var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "8080";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}{ReadyPath}");
            Console.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Health probe failed: {ex.Message}");
            return 1;
        }
    }
}

public sealed class VerificationQueueHealthCheck(VerificationQueueService queue) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(queue.IsRunning
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Verification queue background service is not running"));
}
