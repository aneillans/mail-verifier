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

        var query = _db.VerificationJobs
            .AsNoTracking()
            .AsQueryable();

        if (!IsAdminUser)
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                Jobs = new List<JobListItem>();
                return;
            }

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Jobs = await query
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
        var job = await _db.VerificationJobs
            .Include(j => j.Results)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
        {
            return NotFound();
        }

        // Check authorization - users can only delete their own jobs, admins can delete all
        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId) || job.UploadedByUser != userId)
            {
                return Forbid();
            }
        }

        // Only allow deletion if job is stopped
        if (job.Status != "Stopped")
        {
            Message = "Can only delete stopped jobs";
            return RedirectToPage();
        }

        // Delete results first (foreign key constraint)
        _db.VerificationResults.RemoveRange(job.Results);

        // Delete the job
        _db.VerificationJobs.Remove(job);
        await _db.SaveChangesAsync();

        Message = $"Job #{job.Id} has been deleted";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestartJobAsync(int id)
    {
        var job = await _db.VerificationJobs
            .Include(j => j.Results)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job == null)
        {
            return NotFound();
        }

        // Check authorization - users can only restart their own jobs, admins can restart all
        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId) || job.UploadedByUser != userId)
            {
                return Forbid();
            }
        }

        // Only allow restart if job has no results
        if (job.Results.Count > 0)
        {
            Message = "Can only restart jobs with no results";
            return RedirectToPage();
        }

        // Reset job for processing
        job.Status = "Pending";
        job.ProcessedEmails = 0;

        _db.VerificationJobs.Update(job);
        await _db.SaveChangesAsync();

        _queue.EnqueueJob(job.Id);

        Message = $"Job #{job.Id} has been restarted and queued for processing";
        return RedirectToPage();
    }
}
