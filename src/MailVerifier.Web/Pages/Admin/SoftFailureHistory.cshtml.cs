using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Admin;

[Authorize]
public class SoftFailureHistoryModel : PageModel
{
    private readonly AppDbContext _db;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public bool Searched { get; set; }

    public RecipientSummary? Recipient { get; set; }

    public List<HistoryRow> Events { get; set; } = new();

    public SoftFailureHistoryModel(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!UserAccess.IsAdmin(User))
            return Forbid();

        if (string.IsNullOrWhiteSpace(Q))
            return Page();

        Searched = true;
        var email = Q.Trim().ToLowerInvariant();

        Recipient = await _db.SoftFailureRecipients
            .AsNoTracking()
            .Where(r => r.EmailAddress == email)
            .Select(r => new RecipientSummary
            {
                RecipientId = r.Id,
                EmailAddress = r.EmailAddress,
                LastSeenAt = r.LastSeenAt,
                EventCount = r.Events.Count
            })
            .FirstOrDefaultAsync();

        if (Recipient == null)
            return Page();

        Events = await _db.SoftFailureEvents
            .AsNoTracking()
            .Where(e => e.RecipientId == Recipient.RecipientId)
            .OrderByDescending(e => e.RecordedAt)
            .Select(e => new HistoryRow
            {
                EventId = e.Id,
                RecordedAt = e.RecordedAt,
                ErrorCode = e.ErrorCode,
                Response = e.Response,
                UploadBatchId = e.UploadBatchId,
                UploadFileName = e.UploadBatch.FileName,
                UploadedAt = e.UploadBatch.UploadedAt,
                UploadedByName = !string.IsNullOrWhiteSpace(e.UploadBatch.UploadedByName)
                    ? e.UploadBatch.UploadedByName
                    : e.UploadBatch.UploadedByUser
            })
            .ToListAsync();

        return Page();
    }

    public sealed class RecipientSummary
    {
        public int RecipientId { get; init; }
        public string EmailAddress { get; init; } = string.Empty;
        public DateTime LastSeenAt { get; init; }
        public int EventCount { get; init; }
    }

    public sealed class HistoryRow
    {
        public int EventId { get; init; }
        public DateTime RecordedAt { get; init; }
        public string? ErrorCode { get; init; }
        public string? Response { get; init; }
        public int UploadBatchId { get; init; }
        public string? UploadFileName { get; init; }
        public DateTime UploadedAt { get; init; }
        public string UploadedByName { get; init; } = string.Empty;
    }
}
