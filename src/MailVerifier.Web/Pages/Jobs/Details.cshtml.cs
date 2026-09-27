using System.Globalization;
using CsvHelper;
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

    public List<ResultSummary> Results { get; set; } = new();

    public string EstimatedTimeRemaining
    {
        get
        {
            if (Job == null || Job.ProcessedEmails == 0 || Job.Status == JobStatus.Completed)
                return "—";

            var remainingEmails = Job.TotalEmails - Job.ProcessedEmails;
            if (remainingEmails <= 0)
                return "—";

            var elapsedTime = DateTime.UtcNow - Job.CreatedAt;
            var averageTimePerEmail = elapsedTime.TotalSeconds / Job.ProcessedEmails;
            var estimatedTimeRemaining = TimeSpan.FromSeconds(averageTimePerEmail * remainingEmails);

            if (estimatedTimeRemaining.TotalHours >= 1)
                return $"{(int)estimatedTimeRemaining.TotalHours}h {estimatedTimeRemaining.Minutes}m";
            if (estimatedTimeRemaining.TotalMinutes >= 1)
                return $"{(int)estimatedTimeRemaining.TotalMinutes}m {estimatedTimeRemaining.Seconds}s";
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
        Job = await FindJobAsync(id, tracked: false);
        if (Job == null)
            return NotFound();

        Results = await _db.VerificationResults
            .AsNoTracking()
            .Where(r => r.JobId == id)
            .OrderBy(r => r.EmailAddress)
            .SelectSummaries()
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostDownloadCsvAsync(int id, bool excludeAtRisk = false)
    {
        if (await FindJobAsync(id, tracked: false) == null)
            return NotFound();

        var results = await _db.VerificationResults
            .AsNoTracking()
            .Where(r => r.JobId == id)
            .OrderBy(r => r.EmailAddress)
            .SelectSummaries()
            .Select(s => s.Result)
            .ToListAsync();

        using var buffer = new MemoryStream();
        await using (var writer = new StreamWriter(buffer, leaveOpen: true))
        await using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
        {
            foreach (var header in new[] { "Email", "DomainExists", "HasMxRecords", "MailboxExists", "Verified", "CommonMailbox", "CatchAll", "AtRisk", "PotentialSoftFailure", "SoftFailureNote" })
                csv.WriteField(header);
            await csv.NextRecordAsync();

            foreach (var result in results.Where(r => !excludeAtRisk || !r.IsAtRisk))
            {
                csv.WriteField(CsvSafe(result.EmailAddress));
                csv.WriteField(result.DomainExists);
                csv.WriteField(result.HasMxRecords);
                csv.WriteField(result.MailboxExists);
                csv.WriteField(result.IsVerified);
                csv.WriteField(result.IsCommonMailbox);
                csv.WriteField(result.IsCatchAll);
                csv.WriteField(result.IsAtRisk);
                csv.WriteField(result.IsPotentialSoftFailure);
                csv.WriteField(CsvSafe(result.SoftFailureNote));
                await csv.NextRecordAsync();
            }
        }

        var fileName = $"job-{id}-results-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(buffer.ToArray(), "text/csv", fileName);
    }

    public async Task<IActionResult> OnPostRerunTimeoutsAsync(int id)
    {
        Job = await FindJobAsync(id, tracked: true);
        if (Job == null)
            return NotFound();

        if (JobStatus.IsActive(Job.Status))
        {
            TempData["InfoMessage"] = "The job is still running.";
            return RedirectToPage(new { id });
        }

        var failedResults = await _db.VerificationResults
            .Where(r => r.JobId == id && r.ErrorMessage != null && r.ErrorMessage != "")
            .ToListAsync();

        if (failedResults.Count == 0)
        {
            TempData["InfoMessage"] = "No timed-out results found to rerun.";
            return RedirectToPage(new { id });
        }

        foreach (var result in failedResults)
        {
            // Preserve the first run's outcome for comparison.
            if (!result.IsRetested)
            {
                result.OriginalDomainExists = result.DomainExists;
                result.OriginalHasMxRecords = result.HasMxRecords;
                result.OriginalMailboxExists = result.MailboxExists;
                result.FirstTestedAt = result.VerifiedAt;
            }

            result.IsRetested = true;
            result.PendingRetest = true;
            result.ErrorMessage = null;
            result.VerifiedAt = DateTime.UtcNow;
        }

        Job.Status = JobStatus.Pending;
        Job.ProcessedEmails = Math.Max(0, Job.ProcessedEmails - failedResults.Count);
        await _db.SaveChangesAsync();

        // Only results flagged PendingRetest are re-verified.
        _queueService.EnqueueJob(id);

        TempData["SuccessMessage"] = $"Marked {failedResults.Count} email(s) for retest. They will be re-verified shortly.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStopJobAsync(int id)
    {
        Job = await FindJobAsync(id, tracked: false);
        if (Job == null)
            return NotFound();

        // The queue checks the status between batches and cancels in-flight verification.
        var stopped = await _db.VerificationJobs
            .Where(j => j.Id == id && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatus.Stopped));

        if (stopped > 0)
            TempData["SuccessMessage"] = "Job stopped successfully.";
        else
            TempData["InfoMessage"] = $"Cannot stop job with status '{Job.Status}'.";

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteResultsAsync(int id)
    {
        Job = await FindJobAsync(id, tracked: false);
        if (Job == null)
            return NotFound();

        if (JobStatus.IsActive(Job.Status))
        {
            TempData["InfoMessage"] = "Stop the job before deleting its results.";
            return RedirectToPage(new { id });
        }

        var deleted = await _db.VerificationResults.Where(r => r.JobId == id).ExecuteDeleteAsync();
        if (deleted > 0)
        {
            await _db.VerificationJobs
                .Where(j => j.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ProcessedEmails, 0));
            TempData["SuccessMessage"] = $"Deleted {deleted} result(s).";
        }
        else
        {
            TempData["InfoMessage"] = "No results to delete.";
        }

        return RedirectToPage(new { id });
    }

    private Task<VerificationJob?> FindJobAsync(int id, bool tracked)
    {
        var jobs = tracked ? _db.VerificationJobs : _db.VerificationJobs.AsNoTracking();
        return jobs.AccessibleTo(User).FirstOrDefaultAsync(j => j.Id == id);
    }

    /// <summary>Stops spreadsheet apps from evaluating a cell as a formula (CSV injection).</summary>
    internal static string? CsvSafe(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
}
