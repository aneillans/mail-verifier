using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;

namespace MailVerifier.Web.Pages.Jobs;

public class JobsIndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VerificationQueueService _queue;

    public List<JobListItem> Jobs { get; set; } = new();
    public bool IsAdminUser { get; private set; }

    public sealed class JobListItem
    {
        public int Id { get; init; }
        public string? Name { get; init; }
        public string UploadedByUser { get; init; } = string.Empty;
        public string UploadedByName { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public int TotalEmails { get; init; }
        public int ProcessedEmails { get; init; }
        public string Status { get; init; } = string.Empty;
        public int ValidEmails { get; init; }
        public int ResultCount { get; init; }
    }

    [TempData]
    public string? Message { get; set; }

    public JobsIndexModel(AppDbContext db, VerificationQueueService queue)
    {
        _db = db;
        _queue = queue;
    }

    public async Task OnGetAsync()
    {
        IsAdminUser = UserAccess.IsAdmin(User);

        Jobs = await _db.VerificationJobs
            .AsNoTracking()
            .AccessibleTo(User)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new JobListItem
            {
                Id = j.Id,
                Name = j.Name,
                UploadedByUser = j.UploadedByUser,
                UploadedByName = !string.IsNullOrWhiteSpace(j.UploadedByName) ? j.UploadedByName! : j.UploadedByUser,
                CreatedAt = j.CreatedAt,
                TotalEmails = j.TotalEmails,
                ProcessedEmails = j.ProcessedEmails,
                Status = j.Status,
                ValidEmails = j.Results.Count(r => r.MailboxExists),
                ResultCount = j.Results.Count()
            })
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostDeleteJobAsync(int id)
    {
        // Users can delete their own jobs, admins any job; only stopped jobs can be deleted.
        var job = await _db.VerificationJobs
            .AsNoTracking()
            .AccessibleTo(User)
            .Where(j => j.Id == id)
            .Select(j => new { j.Status })
            .FirstOrDefaultAsync();

        if (job == null)
            return NotFound();

        if (job.Status != JobStatus.Stopped)
        {
            Message = "Can only delete stopped jobs";
            return RedirectToPage();
        }

        // Results and job emails are removed by the database's ON DELETE CASCADE.
        await _db.VerificationJobs.Where(j => j.Id == id).ExecuteDeleteAsync();

        Message = $"Job #{id} has been deleted";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestartJobAsync(int id)
    {
        var job = await _db.VerificationJobs
            .AccessibleTo(User)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
            return NotFound();

        if (JobStatus.IsActive(job.Status))
        {
            Message = "Job is already queued or running";
            return RedirectToPage();
        }

        if (await _db.VerificationResults.AnyAsync(r => r.JobId == id))
        {
            Message = "Can only restart jobs with no results";
            return RedirectToPage();
        }

        job.Status = JobStatus.Pending;
        job.ProcessedEmails = 0;
        await _db.SaveChangesAsync();

        _queue.EnqueueJob(job.Id);

        Message = $"Job #{job.Id} has been restarted and queued for processing";
        return RedirectToPage();
    }
}
