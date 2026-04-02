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

    public List<VerificationJob> Jobs { get; set; } = new();

    [TempData]
    public string? Message { get; set; }

    public JobsIndexModel(AppDbContext db, VerificationQueueService queue)
    {
        _db = db;
        _queue = queue;
    }

    public async Task OnGetAsync()
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
            {
                Jobs = new List<VerificationJob>();
                return;
            }

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Jobs = await query
            .OrderByDescending(j => j.CreatedAt)
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
