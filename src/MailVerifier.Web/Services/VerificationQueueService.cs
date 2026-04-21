using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Services;

public class VerificationQueueService : BackgroundService
{
    private readonly Channel<int> _jobQueue = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VerificationQueueService> _logger;
    private const int BatchSize = 10;

    public VerificationQueueService(IServiceScopeFactory scopeFactory, ILogger<VerificationQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Enqueues a job for background processing.</summary>
    public void EnqueueJob(int jobId)
    {
        _jobQueue.Writer.TryWrite(jobId);
        _logger.LogInformation("Job {JobId} enqueued for verification", jobId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VerificationQueueService started");

        await foreach (var jobId in _jobQueue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessJobAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Unhandled error processing job {JobId}", jobId);
            }
        }
    }

    private async Task ProcessJobAsync(int jobId, CancellationToken ct)
    {
        _logger.LogInformation("Starting processing of job {JobId}", jobId);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifier = scope.ServiceProvider.GetRequiredService<EmailVerificationService>();

        var job = await db.VerificationJobs.FindAsync(new object[] { jobId }, ct);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found in database", jobId);
            return;
        }

        job.Status = "Processing";
        await db.SaveChangesAsync(ct);

        try
        {
            var emails = await db.JobEmails
                .Where(e => e.JobId == jobId)
                .Select(e => e.EmailAddress)
                .ToListAsync(ct);

            int processed = 0;

            for (int i = 0; i < emails.Count; i += BatchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = emails.Skip(i).Take(BatchSize).ToList();

                // Verify emails in the batch concurrently
                var tasks = batch.Select(email => verifier.VerifyEmailAsync(email));
                var results = await Task.WhenAll(tasks);

                var softFailureNotes = await db.SoftFailureRecipients
                    .AsNoTracking()
                    .Where(r => batch.Contains(r.EmailAddress))
                    .Select(r => new
                    {
                        r.EmailAddress,
                        LatestCode = r.Events
                            .OrderByDescending(e => e.RecordedAt)
                            .Select(e => e.ErrorCode)
                            .FirstOrDefault(),
                        LatestResponse = r.Events
                            .OrderByDescending(e => e.RecordedAt)
                            .Select(e => e.Response)
                            .FirstOrDefault()
                    })
                    .ToDictionaryAsync(
                        x => x.EmailAddress,
                        x => BuildSoftFailureNote(x.LatestCode, x.LatestResponse),
                        StringComparer.OrdinalIgnoreCase,
                        ct);

                // Load all existing results for this batch in one query instead of N individual lookups
                var existingByEmail = await db.VerificationResults
                    .Where(r => r.JobId == jobId && batch.Contains(r.EmailAddress))
                    .ToDictionaryAsync(r => r.EmailAddress, StringComparer.OrdinalIgnoreCase, ct);

                foreach (var result in results)
                {
                    result.JobId = jobId;
                    result.IsPotentialSoftFailure = softFailureNotes.ContainsKey(result.EmailAddress);
                    result.SoftFailureNote = result.IsPotentialSoftFailure
                        ? softFailureNotes[result.EmailAddress]
                        : null;

                    if (existingByEmail.TryGetValue(result.EmailAddress, out var existingResult))
                    {
                        // Update the existing result (in case it had an error before)
                        existingResult.DomainExists = result.DomainExists;
                        existingResult.HasMxRecords = result.HasMxRecords;
                        existingResult.MailboxExists = result.MailboxExists;
                        existingResult.ErrorMessage = result.ErrorMessage;
                        existingResult.IsPotentialSoftFailure = result.IsPotentialSoftFailure;
                        existingResult.SoftFailureNote = result.SoftFailureNote;
                        db.VerificationResults.Update(existingResult);
                    }
                    else
                    {
                        db.VerificationResults.Add(result);
                    }
                }

                processed += batch.Count;
                job.ProcessedEmails = processed;
                await db.SaveChangesAsync(ct);

                _logger.LogInformation("Job {JobId}: {Processed}/{Total} emails processed",
                    jobId, processed, job.TotalEmails);
            }

            job.Status = "Completed";
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Job {JobId} completed", jobId);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown — reset to Pending so the job is re-queued on next startup
            job.Status = "Pending";
            job.ProcessedEmails = 0;
            await db.SaveChangesAsync(CancellationToken.None);
            _logger.LogWarning("Job {JobId} interrupted by shutdown; reset to Pending for retry on restart", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", jobId);
            job.Status = "Failed";
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private static string? BuildSoftFailureNote(string? code, string? response)
    {
        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(response))
            return null;

        if (string.IsNullOrWhiteSpace(code))
            return response;

        if (string.IsNullOrWhiteSpace(response))
            return $"Code {code}";

        return $"Code {code}: {response}";
    }
}
