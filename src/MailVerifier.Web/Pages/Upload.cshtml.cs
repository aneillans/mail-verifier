using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MailVerifier.Web.Security;
using MailVerifier.Web.Services;

namespace MailVerifier.Web.Pages;

public class UploadModel : PageModel
{
    private readonly JobCreationService _jobCreation;
    private readonly ILogger<UploadModel> _logger;

    public string? ErrorMessage { get; set; }

    public UploadModel(JobCreationService jobCreation, ILogger<UploadModel> logger)
    {
        _jobCreation = jobCreation;
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

        if (UserAccess.GetUserId(User) == null)
            return Forbid();

        var job = await _jobCreation.CreateAndEnqueueAsync(jobName, emails, User, HttpContext.RequestAborted);
        if (job == null)
        {
            ErrorMessage = "No valid email addresses found in the file.";
            return Page();
        }

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
