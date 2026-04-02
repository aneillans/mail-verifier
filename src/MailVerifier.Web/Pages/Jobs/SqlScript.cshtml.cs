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
    }

    public VerificationJob? Job { get; set; }

    public List<SqlEmailRow> EmailRows { get; set; } = new();

    public JobSqlScriptModel(AppDbContext db)
    {
        _db = db;
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

        if (Job.Status != "Completed")
        {
            TempData["InfoMessage"] = "SQL generation is only available for completed jobs.";
            return RedirectToPage("/Jobs/Details", new { id });
        }

        EmailRows = Job.Results
            .OrderBy(r => r.EmailAddress)
            .Select(r => new SqlEmailRow
            {
                EmailAddress = r.EmailAddress,
                IsVerified = r.IsVerified
            })
            .ToList();

        return Page();
    }
}
