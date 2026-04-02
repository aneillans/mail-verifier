using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;

namespace MailVerifier.Web.Services;

public class DataRetentionService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DataRetentionService> _logger;
    private readonly int _retentionDays;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(24);

    public DataRetentionService(
        IServiceProvider serviceProvider,
        ILogger<DataRetentionService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _retentionDays = configuration.GetValue<int>("DataRetention:RetentionDays", 7);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataRetentionService started. Retention: {Days} days", _retentionDays);

        // Initial delay to avoid purging during application startup
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeOldDataAsync();
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task PurgeOldDataAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);

            var oldJobs = await db.VerificationJobs
                .Where(j => j.CreatedAt < cutoff)
                .ToListAsync();

            if (oldJobs.Count > 0)
            {
                db.VerificationJobs.RemoveRange(oldJobs);
                await db.SaveChangesAsync();
                _logger.LogInformation("Purged {Count} verification jobs older than {Days} days", oldJobs.Count, _retentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during data retention purge");
        }
    }
}
