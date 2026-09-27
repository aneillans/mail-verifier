using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Services;

public class VerificationQueueService : BackgroundService
{
    private const int PersistBatchSize = 25;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly Channel<int> _jobQueue = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VerificationQueueService> _logger;

    public VerificationQueueService(IServiceScopeFactory scopeFactory, ILogger<VerificationQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>True while the background loop is alive; used by the health check.</summary>
    public bool IsRunning => ExecuteTask is { IsCompleted: false };

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

    private async Task ProcessJobAsync(int jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var verifier = scope.ServiceProvider.GetRequiredService<EmailVerificationService>();

        var job = await db.VerificationJobs.FindAsync(new object[] { jobId }, stoppingToken);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found in database", jobId);
            return;
        }

        if (job.Status == JobStatus.Stopped)
        {
            _logger.LogInformation("Job {JobId} was stopped before processing started; skipping", jobId);
            return;
        }

        _logger.LogInformation("Starting processing of job {JobId}", jobId);

        try
        {
            var emails = EmailAddressDeduplicator.Deduplicate(
                await db.JobEmails
                    .Where(e => e.JobId == jobId)
                    .Select(e => e.EmailAddress)
                    .ToListAsync(stoppingToken));

            // Resume: only verify addresses without a result, plus results queued by "Rerun".
            var completed = (await db.VerificationResults
                    .Where(r => r.JobId == jobId && !r.PendingRetest)
                    .Select(r => r.EmailAddress)
                    .ToListAsync(stoppingToken))
                .ToHashSet(StringComparer.Ordinal);

            var pending = emails.Where(e => !completed.Contains(e)).ToList();

            job.Status = JobStatus.Processing;
            job.TotalEmails = emails.Count;
            job.ProcessedEmails = emails.Count - pending.Count;
            await db.SaveChangesAsync(stoppingToken);
            var processed = job.ProcessedEmails;
            db.ChangeTracker.Clear();

            _logger.LogInformation("Job {JobId}: {Pending} of {Total} emails to verify", jobId, pending.Count, emails.Count);

            using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var results = Channel.CreateUnbounded<VerificationResult>(new UnboundedChannelOptions { SingleReader = true });

            var producer = Task.Run(async () =>
            {
                try
                {
                    await verifier.VerifyAsync(pending, (r, t) => results.Writer.WriteAsync(r, t), verifyCts.Token);
                    results.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    results.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);

            var buffer = new List<VerificationResult>(PersistBatchSize);
            var stopped = false;

            async Task FlushAsync()
            {
                if (buffer.Count == 0)
                    return;

                await PersistAsync(db, jobId, buffer, stoppingToken);
                processed += buffer.Count;
                buffer.Clear();

                await db.VerificationJobs
                    .Where(j => j.Id == jobId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProcessedEmails, processed), stoppingToken);

                _logger.LogInformation("Job {JobId}: {Processed}/{Total} emails processed", jobId, processed, emails.Count);
            }

            using (var timer = new PeriodicTimer(FlushInterval))
            {
                Task<bool>? readTask = null;
                Task<bool>? tickTask = null;

                while (true)
                {
                    readTask ??= results.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    tickTask ??= timer.WaitForNextTickAsync(stoppingToken).AsTask();

                    if (await Task.WhenAny(readTask, tickTask) == readTask)
                    {
                        var more = await readTask; // rethrows if verification faulted
                        readTask = null;
                        if (!more)
                            break;

                        while (results.Reader.TryRead(out var result))
                            buffer.Add(result);

                        if (buffer.Count >= PersistBatchSize)
                            await FlushAsync();
                        continue;
                    }

                    tickTask = null;
                    await FlushAsync();

                    if (await IsStopRequestedAsync(db, jobId, stoppingToken))
                    {
                        stopped = true;
                        await verifyCts.CancelAsync();
                        break;
                    }
                }
            }

            await producer;

            if (stopped)
            {
                // Keep whatever finished before the cancellation took effect.
                while (results.Reader.TryRead(out var result))
                    buffer.Add(result);
                await FlushAsync();
                _logger.LogInformation("Job {JobId} stopped by user after {Processed}/{Total} emails", jobId, processed, emails.Count);
                return;
            }

            await FlushAsync();
            await SetStatusIfProcessingAsync(db, jobId, JobStatus.Completed, CancellationToken.None);
            _logger.LogInformation("Job {JobId} completed", jobId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown: back to Pending so startup re-queues it; finished results are kept and skipped.
            await SetStatusIfProcessingAsync(db, jobId, JobStatus.Pending, CancellationToken.None);
            _logger.LogWarning("Job {JobId} interrupted by shutdown; will resume on restart", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {JobId}", jobId);
            await SetStatusIfProcessingAsync(db, jobId, JobStatus.Failed, CancellationToken.None);
        }
    }

    private async Task PersistAsync(AppDbContext db, int jobId, List<VerificationResult> batch, CancellationToken ct)
    {
        var emails = batch.Select(r => r.EmailAddress).ToList();

        var softFailureNotes = await db.SoftFailureRecipients
            .AsNoTracking()
            .Where(r => emails.Contains(r.EmailAddress))
            .Select(r => new
            {
                r.EmailAddress,
                Latest = r.Events
                    .OrderByDescending(e => e.RecordedAt)
                    .Select(e => new { e.ErrorCode, e.Response })
                    .FirstOrDefault()
            })
            .ToDictionaryAsync(
                x => x.EmailAddress,
                x => BuildSoftFailureNote(x.Latest?.ErrorCode, x.Latest?.Response),
                StringComparer.Ordinal,
                ct);

        var existingByEmail = await db.VerificationResults
            .Where(r => r.JobId == jobId && emails.Contains(r.EmailAddress))
            .ToDictionaryAsync(r => r.EmailAddress, StringComparer.Ordinal, ct);

        // Stamp with persist time so the progress endpoint's "since" cursor only moves forward.
        var now = DateTime.UtcNow;

        foreach (var result in batch)
        {
            result.JobId = jobId;
            result.VerifiedAt = now;
            result.IsPotentialSoftFailure = softFailureNotes.TryGetValue(result.EmailAddress, out var note);
            result.SoftFailureNote = note;

            if (existingByEmail.TryGetValue(result.EmailAddress, out var existing))
            {
                existing.DomainExists = result.DomainExists;
                existing.HasMxRecords = result.HasMxRecords;
                existing.MailboxExists = result.MailboxExists;
                existing.ErrorMessage = result.ErrorMessage;
                existing.SmtpLog = result.SmtpLog;
                existing.VerifiedAt = result.VerifiedAt;
                existing.FirstTestedAt ??= result.FirstTestedAt;
                existing.IsCatchAll = result.IsCatchAll;
                existing.IsPotentialSoftFailure = result.IsPotentialSoftFailure;
                existing.SoftFailureNote = result.SoftFailureNote;
                existing.PendingRetest = false;
            }
            else
            {
                db.VerificationResults.Add(result);
            }
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        // A definitive rejection (5xx on RCPT) supersedes the "soft failure" signal for that recipient.
        var invalidEmails = batch
            .Where(r => r.IsInvalidMailbox)
            .Select(r => r.EmailAddress)
            .ToList();

        if (invalidEmails.Count > 0)
        {
            var removed = await db.SoftFailureRecipients
                .Where(r => invalidEmails.Contains(r.EmailAddress))
                .ExecuteDeleteAsync(ct);

            if (removed > 0)
                _logger.LogInformation("Job {JobId}: Removed {Count} invalid mailboxes from soft-failure list", jobId, removed);
        }
    }

    private static async Task<bool> IsStopRequestedAsync(AppDbContext db, int jobId, CancellationToken ct)
    {
        var status = await db.VerificationJobs
            .Where(j => j.Id == jobId)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(ct);

        return status is null or JobStatus.Stopped;
    }

    /// <summary>Changes status only while the job is still Processing, so a user's Stop is never overwritten.</summary>
    private static Task SetStatusIfProcessingAsync(AppDbContext db, int jobId, string status, CancellationToken ct) =>
        db.VerificationJobs
            .Where(j => j.Id == jobId && j.Status == JobStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status), ct);

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
