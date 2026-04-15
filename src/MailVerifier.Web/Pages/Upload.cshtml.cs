using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MailVerifier.Web.Data;
using MailVerifier.Web.Models;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;

namespace MailVerifier.Web.Pages;

public class UploadModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly VerificationQueueService _queueService;
    private readonly ILogger<UploadModel> _logger;

    public string? ErrorMessage { get; set; }

    public UploadModel(AppDbContext db, VerificationQueueService queueService, ILogger<UploadModel> logger)
    {
        _db = db;
        _queueService = queueService;
        _logger = logger;
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(IFormFile csvFile, string? jobName)
    {
        if (csvFile == null || csvFile.Length == 0)
        {
            ErrorMessage = "Please select a CSV file to upload.";
            return Page();
        }

        List<string> emails;
        try
        {
            emails = ParseEmails(csvFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse CSV file");
            ErrorMessage = $"Failed to parse CSV file: {ex.Message}";
            return Page();
        }

        if (emails.Count == 0)
        {
            ErrorMessage = "No valid email addresses found in the file.";
            return Page();
        }

        var userId = UserAccess.GetUserId(User);
        if (string.IsNullOrWhiteSpace(userId))
            return Forbid();

        var userDisplayName = UserAccess.GetUserDisplayName(User);

        // Create the job record
        var job = new VerificationJob
        {
            Name = string.IsNullOrWhiteSpace(jobName) ? null : jobName.Trim(),
            UploadedByUser = userId,
            UploadedByName = string.IsNullOrWhiteSpace(userDisplayName) ? null : userDisplayName.Trim(),
            CreatedAt = DateTime.UtcNow,
            TotalEmails = emails.Count,
            ProcessedEmails = 0,
            Status = "Pending"
        };

        _db.VerificationJobs.Add(job);
        await _db.SaveChangesAsync();

        // Store the emails to be processed by the background service
        foreach (var email in emails)
        {
            _db.JobEmails.Add(new JobEmail
            {
                JobId = job.Id,
                EmailAddress = email.Trim()
            });
        }
        await _db.SaveChangesAsync();

        // Enqueue the job for background batch processing (10 at a time)
        _queueService.EnqueueJob(job.Id);

        // Redirect immediately — the user will see live progress on the details page
        return RedirectToPage("/Jobs/Details", new { id = job.Id });
    }

    private static List<string> ParseEmails(IFormFile file)
    {
        var emails = new List<string>();

        using var reader = new StreamReader(file.OpenReadStream());
        var content = reader.ReadToEnd();

        // Try CSV with header first
        try
        {
            using var stringReader = new StringReader(content);
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                MissingFieldFound = null,
                HeaderValidated = null,
                BadDataFound = null
            };
            using var csvReader = new CsvReader(stringReader, config);
            csvReader.Read();
            csvReader.ReadHeader();

            var headers = csvReader.HeaderRecord;
            if (headers != null && headers.Any(h => h.Trim().Equals("email", StringComparison.OrdinalIgnoreCase)))
            {
                while (csvReader.Read())
                {
                    var email = csvReader.GetField("email")?.Trim();
                    if (!string.IsNullOrWhiteSpace(email))
                        emails.Add(email);
                }
                return emails;
            }
        }
        catch
        {
            // Fall through to line-by-line
        }

        // Line-by-line fallback
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim().Trim('\r');
            if (!string.IsNullOrWhiteSpace(trimmed) && trimmed.Contains('@'))
                emails.Add(trimmed);
        }

        return emails;
    }
}
