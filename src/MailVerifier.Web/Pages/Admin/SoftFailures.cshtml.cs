using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Pages.Admin;

[Authorize]
public class SoftFailuresModel : PageModel
{
    private static readonly string[] EmailHeaders = ["to", "email"];
    private static readonly string[] ResponseHeaders = ["response", "error"];
    private static readonly string[] DeliveredHeaders = ["delivered"];
    private static readonly string[] CodeHeaders = ["code", "errorcode"];

    private readonly AppDbContext _db;
    private readonly ILogger<SoftFailuresModel> _logger;

    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public int ActiveRecipientCount { get; set; }

    public List<SoftFailureUploadBatch> RecentUploads { get; set; } = new();

    public SoftFailuresModel(AppDbContext db, ILogger<SoftFailuresModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!UserAccess.IsAdmin(User))
            return Forbid();

        await LoadSummaryAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile csvFile)
    {
        if (!UserAccess.IsAdmin(User))
            return Forbid();

        if (csvFile == null || csvFile.Length == 0)
        {
            ErrorMessage = "Please select a CSV file to upload.";
            await LoadSummaryAsync();
            return Page();
        }

        List<ImportRow> rows;
        try
        {
            rows = ParseRows(csvFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse soft-failure CSV upload");
            ErrorMessage = $"Failed to parse CSV: {ex.Message}";
            await LoadSummaryAsync();
            return Page();
        }

        if (rows.Count == 0)
        {
            ErrorMessage = "No usable rows were found in the CSV.";
            await LoadSummaryAsync();
            return Page();
        }

        var userId = UserAccess.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Forbid();

        var userDisplayName = UserAccess.GetUserDisplayName(User);
        var now = DateTime.UtcNow;

        var successEmails = rows
            .Where(r => r.Delivered)
            .Select(r => r.EmailAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var failureRows = rows
            .Where(r => !r.Delivered)
            .Where(r => !successEmails.Contains(r.EmailAddress))
            .ToList();

        await using var tx = await _db.Database.BeginTransactionAsync();

        var upload = new SoftFailureUploadBatch
        {
            FileName = Path.GetFileName(csvFile.FileName),
            UploadedByUser = userId,
            UploadedByName = string.IsNullOrWhiteSpace(userDisplayName) ? null : userDisplayName.Trim(),
            UploadedAt = now,
            TotalRows = rows.Count,
            SuccessRowsApplied = successEmails.Count
        };

        _db.SoftFailureUploadBatches.Add(upload);
        await _db.SaveChangesAsync();

        var recipientsToRemove = await FindRecipientsByEmailsAsync(successEmails);
        if (recipientsToRemove.Count > 0)
        {
            upload.RecipientRemovals = recipientsToRemove.Count;
            _db.SoftFailureRecipients.RemoveRange(recipientsToRemove);
        }

        if (failureRows.Count > 0)
        {
            var failureEmails = failureRows
                .Select(r => r.EmailAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingRecipients = await FindRecipientDictionaryAsync(failureEmails);

            var newRecipients = failureEmails
                .Where(email => !existingRecipients.ContainsKey(email))
                .Select(email => new SoftFailureRecipient
                {
                    EmailAddress = email,
                    LastSeenAt = now
                })
                .ToList();

            if (newRecipients.Count > 0)
            {
                _db.SoftFailureRecipients.AddRange(newRecipients);
                await _db.SaveChangesAsync();

                foreach (var recipient in newRecipients)
                {
                    existingRecipients[recipient.EmailAddress] = recipient;
                }
            }

            foreach (var recipient in existingRecipients.Values)
            {
                recipient.LastSeenAt = now;
            }

            var events = new List<SoftFailureEvent>(failureRows.Count);
            foreach (var row in failureRows)
            {
                if (!existingRecipients.TryGetValue(row.EmailAddress, out var recipient))
                    continue;

                events.Add(new SoftFailureEvent
                {
                    RecipientId = recipient.Id,
                    UploadBatchId = upload.Id,
                    ErrorCode = row.ErrorCode,
                    Response = row.Response,
                    RecordedAt = now
                });
            }

            if (events.Count > 0)
            {
                _db.SoftFailureEvents.AddRange(events);
                upload.FailureRowsRecorded = events.Count;
            }
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        SuccessMessage =
            $"Import complete. Rows: {upload.TotalRows}, active soft-failure recipients removed: {upload.RecipientRemovals}, failure events added: {upload.FailureRowsRecorded}.";

        return RedirectToPage();
    }

    private async Task LoadSummaryAsync()
    {
        ActiveRecipientCount = await _db.SoftFailureRecipients.AsNoTracking().CountAsync();
        RecentUploads = await _db.SoftFailureUploadBatches
            .AsNoTracking()
            .OrderByDescending(x => x.UploadedAt)
            .Take(10)
            .ToListAsync();
    }

    private async Task<List<SoftFailureRecipient>> FindRecipientsByEmailsAsync(HashSet<string> emails)
    {
        var recipients = new List<SoftFailureRecipient>();

        foreach (var chunk in Chunk(emails, 500))
        {
            var chunkRecipients = await _db.SoftFailureRecipients
                .Where(r => chunk.Contains(r.EmailAddress))
                .ToListAsync();

            recipients.AddRange(chunkRecipients);
        }

        return recipients;
    }

    private async Task<Dictionary<string, SoftFailureRecipient>> FindRecipientDictionaryAsync(List<string> emails)
    {
        var recipients = new Dictionary<string, SoftFailureRecipient>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in Chunk(emails, 500))
        {
            var chunkRecipients = await _db.SoftFailureRecipients
                .Where(r => chunk.Contains(r.EmailAddress))
                .ToListAsync();

            foreach (var recipient in chunkRecipients)
            {
                recipients[recipient.EmailAddress] = recipient;
            }
        }

        return recipients;
    }

    private static List<ImportRow> ParseRows(IFormFile file)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            DetectDelimiter = true
        };

        using var csv = new CsvReader(reader, config);

        if (!csv.Read())
            return new List<ImportRow>();

        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? throw new InvalidOperationException("CSV header row was not found.");

        var emailHeader = ResolveHeader(headers, EmailHeaders)
            ?? throw new InvalidOperationException("CSV must contain one of these columns: to, email");
        var deliveredHeader = ResolveHeader(headers, DeliveredHeaders)
            ?? throw new InvalidOperationException("CSV must contain the delivered column");

        var responseHeader = ResolveHeader(headers, ResponseHeaders);
        var codeHeader = ResolveHeader(headers, CodeHeaders);

        var rows = new List<ImportRow>();

        while (csv.Read())
        {
            var email = NormalizeEmail(csv.GetField(emailHeader));
            if (email == null)
                continue;

            var deliveredRaw = csv.GetField(deliveredHeader);
            var delivered = ParseDelivered(deliveredRaw);

            var response = NormalizeText(responseHeader != null ? csv.GetField(responseHeader) : null, 2048);
            var code = NormalizeText(codeHeader != null ? csv.GetField(codeHeader) : null, 128);

            rows.Add(new ImportRow
            {
                EmailAddress = email,
                Delivered = delivered,
                Response = response,
                ErrorCode = code
            });
        }

        return rows;
    }

    private static string? ResolveHeader(string[] headers, string[] candidates)
    {
        foreach (var header in headers)
        {
            var normalized = header.Trim();
            if (candidates.Any(c => string.Equals(c, normalized, StringComparison.OrdinalIgnoreCase)))
                return header;
        }

        return null;
    }

    private static string? NormalizeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var email = value.Trim().ToLowerInvariant();
        return email.Contains('@') ? email : null;
    }

    private static string? NormalizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool ParseDelivered(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        if (normalized.Equals("1", StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        if (normalized.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static IEnumerable<List<string>> Chunk(IEnumerable<string> source, int size)
    {
        var chunk = new List<string>(size);
        foreach (var item in source)
        {
            chunk.Add(item);
            if (chunk.Count == size)
            {
                yield return chunk;
                chunk = new List<string>(size);
            }
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    private sealed class ImportRow
    {
        public string EmailAddress { get; init; } = string.Empty;
        public bool Delivered { get; init; }
        public string? Response { get; init; }
        public string? ErrorCode { get; init; }
    }
}
