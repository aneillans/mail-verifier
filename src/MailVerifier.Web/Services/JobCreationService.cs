using System.Security.Claims;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Services;

public class JobCreationService
{
    private readonly AppDbContext _db;
    private readonly VerificationQueueService _queue;

    public JobCreationService(AppDbContext db, VerificationQueueService queue)
    {
        _db = db;
        _queue = queue;
    }

    /// <summary>
    /// Stores a new job with its (normalized, de-duplicated) addresses and queues it for verification.
    /// Returns null when the user has no identifier or there are no addresses.
    /// </summary>
    public async Task<VerificationJob?> CreateAndEnqueueAsync(string? name, IEnumerable<string> emails, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = UserAccess.GetUserId(user);
        if (string.IsNullOrWhiteSpace(userId))
            return null;

        var addresses = EmailAddressDeduplicator.Deduplicate(emails);
        if (addresses.Count == 0)
            return null;

        var displayName = UserAccess.GetUserDisplayName(user);
        var job = new VerificationJob
        {
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            UploadedByUser = userId,
            UploadedByName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            CreatedAt = DateTime.UtcNow,
            TotalEmails = addresses.Count,
            Status = JobStatus.Pending,
            JobEmails = addresses.Select(email => new JobEmail { EmailAddress = email }).ToList()
        };

        _db.VerificationJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        _queue.EnqueueJob(job.Id);
        return job;
    }
}
