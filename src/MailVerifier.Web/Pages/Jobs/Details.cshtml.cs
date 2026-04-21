using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;

namespace MailVerifier.Web.Pages.Jobs;

public class JobDetailsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VerificationQueueService _queueService;

    public VerificationJob? Job { get; set; }

    public string EstimatedTimeRemaining
    {
        get
        {
            if (Job == null || Job.ProcessedEmails == 0 || Job.Status == "Completed")
                return "—";

            var remainingEmails = Job.TotalEmails - Job.ProcessedEmails;
            if (remainingEmails <= 0)
                return "—";

            var elapsedTime = DateTime.UtcNow - Job.CreatedAt;
            var averageTimePerEmail = elapsedTime.TotalSeconds / Job.ProcessedEmails;
            var estimatedSecondsRemaining = averageTimePerEmail * remainingEmails;
            var estimatedTimeRemaining = TimeSpan.FromSeconds(estimatedSecondsRemaining);

            // Format as human-readable string
            if (estimatedTimeRemaining.TotalHours >= 1)
                return $"{(int)estimatedTimeRemaining.TotalHours}h {estimatedTimeRemaining.Minutes}m";
            else if (estimatedTimeRemaining.TotalMinutes >= 1)
                return $"{(int)estimatedTimeRemaining.TotalMinutes}m {estimatedTimeRemaining.Seconds}s";
            else
                return $"{(int)estimatedTimeRemaining.TotalSeconds}s";
        }
    }

    public JobDetailsModel(AppDbContext db, VerificationQueueService queueService)
    {
        _db = db;
        _queueService = queueService;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .Where(j => j.Id == id)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound();

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Job = await query.FirstOrDefaultAsync();

        if (Job == null)
            return NotFound();

        return Page();
    }

    public async Task<IActionResult> OnPostDownloadCsvAsync(int id, bool excludeAtRisk = false)
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .Where(j => j.Id == id)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound();

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Job = await query.FirstOrDefaultAsync();
        if (Job == null)
            return NotFound();

        var csv = new StringBuilder();
        csv.AppendLine("Email,DomainExists,HasMxRecords,MailboxExists,Verified,CommonMailbox,AtRisk,PotentialSoftFailure,SoftFailureNote");

        var exportResults = Job.Results
            .Where(r => !excludeAtRisk || !r.IsAtRisk)
            .OrderBy(r => r.EmailAddress);

        foreach (var result in exportResults)
        {
            var softFailureNote = result.SoftFailureNote?.Replace("\"", "\"\"") ?? string.Empty;
            csv.AppendLine($"\"{result.EmailAddress}\",{(result.DomainExists ? "true" : "false")},{(result.HasMxRecords ? "true" : "false")},{(result.MailboxExists ? "true" : "false")},{(result.IsVerified ? "true" : "false")},{(result.IsCommonMailbox ? "true" : "false")},{(result.IsAtRisk ? "true" : "false")},{(result.IsPotentialSoftFailure ? "true" : "false")},\"{softFailureNote}\"");
        }

        var fileName = $"job-{id}-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return File(bytes, "text/csv", fileName);
    }

    public async Task<IActionResult> OnPostRerunTimeoutsAsync(int id)
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .Where(j => j.Id == id)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound();

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Job = await query.FirstOrDefaultAsync();
        if (Job == null)
            return NotFound();

        // Find all results with error messages (timeouts or connection errors)
        var timedOutResults = Job.Results.Where(r => !string.IsNullOrEmpty(r.ErrorMessage)).ToList();

        if (timedOutResults.Count > 0)
        {
            // Preserve original results and mark for retest instead of deleting
            foreach (var result in timedOutResults)
            {
                // Store original values if this is the first retest
                if (!result.IsRetested)
                {
                    result.OriginalDomainExists = result.DomainExists;
                    result.OriginalHasMxRecords = result.HasMxRecords;
                    result.OriginalMailboxExists = result.MailboxExists;
                    result.FirstTestedAt = result.VerifiedAt;
                }

                // Mark as retested and clear error for reprocessing
                result.IsRetested = true;
                result.ErrorMessage = null;
                result.VerifiedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            // Requeue the job
            _queueService.EnqueueJob(Job.Id);

            TempData["SuccessMessage"] = $"Marked {timedOutResults.Count} email(s) for retest. They will be re-verified shortly.";
        }
        else
        {
            TempData["InfoMessage"] = "No timed-out results found to rerun.";
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStopJobAsync(int id)
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .Where(j => j.Id == id)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound();

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Job = await query.FirstOrDefaultAsync();
        if (Job == null)
            return NotFound();

        if (Job.Status is "Pending" or "Processing")
        {
            Job.Status = "Stopped";
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = "Job stopped successfully.";
        }
        else
        {
            TempData["InfoMessage"] = $"Cannot stop job with status '{Job.Status}'.";
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteResultsAsync(int id)
    {
        var query = _db.VerificationJobs
            .Include(j => j.Results)
            .Where(j => j.Id == id)
            .AsQueryable();

        if (!UserAccess.IsAdmin(User))
        {
            var userId = UserAccess.GetUserId(User);
            if (string.IsNullOrWhiteSpace(userId))
                return NotFound();

            query = query.Where(j => j.UploadedByUser == userId);
        }

        Job = await query.FirstOrDefaultAsync();
        if (Job == null)
            return NotFound();

        var resultCount = Job.Results.Count;
        if (resultCount > 0)
        {
            _db.VerificationResults.RemoveRange(Job.Results);
            Job.ProcessedEmails = 0;
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Deleted {resultCount} result(s).";
        }
        else
        {
            TempData["InfoMessage"] = "No results to delete.";
        }

        return RedirectToPage(new { id });
    }
}
