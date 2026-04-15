using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Admin;

[Authorize]
public class EmailSearchModel : PageModel
{
    private readonly AppDbContext _db;

    public sealed class SearchResultRow
    {
        public int ResultId { get; init; }
        public int JobId { get; init; }
        public string? JobName { get; init; }
        public string UploadedByName { get; init; } = string.Empty;
        public bool DomainExists { get; init; }
        public bool HasMxRecords { get; init; }
        public bool MailboxExists { get; init; }
        public bool? OriginalDomainExists { get; init; }
        public bool? OriginalHasMxRecords { get; init; }
        public bool? OriginalMailboxExists { get; init; }
        public bool IsRetested { get; init; }
        public string SmtpLog { get; init; } = string.Empty;
        public string? ErrorMessage { get; init; }
        public DateTime VerifiedAt { get; init; }
        public DateTime? FirstTestedAt { get; init; }

        // Computed helpers (mirrors VerificationResult NotMapped properties)
        public bool IsVerified =>
            DomainExists && HasMxRecords && MailboxExists && string.IsNullOrWhiteSpace(ErrorMessage);

        public bool IsRetryable =>
            !string.IsNullOrWhiteSpace(ErrorMessage) &&
            ErrorMessage.StartsWith("Temporary SMTP response", StringComparison.OrdinalIgnoreCase);
    }

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public List<SearchResultRow> Results { get; set; } = new();
    public bool Searched { get; set; }

    public EmailSearchModel(AppDbContext db)
    {
        _db = db;
    }

    public IActionResult OnGet()
    {
        if (!UserAccess.IsAdmin(User))
            return Forbid();

        if (string.IsNullOrWhiteSpace(Q))
            return Page();

        Searched = true;
        var email = Q.Trim();

        Results = _db.VerificationResults
            .AsNoTracking()
            .Where(r => r.EmailAddress == email)
            .OrderByDescending(r => r.VerifiedAt)
            .Select(r => new SearchResultRow
            {
                ResultId = r.Id,
                JobId = r.JobId,
                JobName = r.Job.Name,
                UploadedByName = !string.IsNullOrWhiteSpace(r.Job.UploadedByName)
                    ? r.Job.UploadedByName
                    : r.Job.UploadedByUser,
                DomainExists = r.DomainExists,
                HasMxRecords = r.HasMxRecords,
                MailboxExists = r.MailboxExists,
                OriginalDomainExists = r.OriginalDomainExists,
                OriginalHasMxRecords = r.OriginalHasMxRecords,
                OriginalMailboxExists = r.OriginalMailboxExists,
                IsRetested = r.IsRetested,
                SmtpLog = r.SmtpLog,
                ErrorMessage = r.ErrorMessage,
                VerifiedAt = r.VerifiedAt,
                FirstTestedAt = r.FirstTestedAt,
            })
            .ToList();

        return Page();
    }
}
