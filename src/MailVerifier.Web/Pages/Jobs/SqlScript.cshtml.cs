using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Jobs;

public class JobSqlScriptModel : PageModel
{
    private readonly AppDbContext _db;

    public sealed class SqlEmailRow
    {
        public string EmailAddress { get; set; } = string.Empty;

        public bool IsVerified { get; set; }

        public bool IsAtRisk { get; set; }
    }

    public VerificationJob? Job { get; set; }

    public List<SqlEmailRow> EmailRows { get; set; } = new();

    public JobSqlScriptModel(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        Job = await _db.VerificationJobs
            .AsNoTracking()
            .AccessibleTo(User)
            .FirstOrDefaultAsync(j => j.Id == id);
        if (Job == null)
            return NotFound();

        if (Job.Status != JobStatus.Completed)
        {
            TempData["InfoMessage"] = "SQL generation is only available for completed jobs.";
            return RedirectToPage("/Jobs/Details", new { id });
        }

        var results = await _db.VerificationResults
            .AsNoTracking()
            .Where(r => r.JobId == id)
            .OrderBy(r => r.EmailAddress)
            .SelectSummaries()
            .Select(s => s.Result)
            .ToListAsync();

        EmailRows = results
            .Select(r => new SqlEmailRow
            {
                EmailAddress = r.EmailAddress,
                IsVerified = r.IsVerified,
                IsAtRisk = r.IsAtRisk
            })
            .ToList();

        return Page();
    }
}
