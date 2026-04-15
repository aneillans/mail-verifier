using System.ComponentModel.DataAnnotations;

namespace MailVerifier.Web.Models;

public class VerificationJob
{
    public int Id { get; set; }

    public string? Name { get; set; }

    [Required]
    public string UploadedByUser { get; set; } = string.Empty;

    public string? UploadedByName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int TotalEmails { get; set; }

    public int ProcessedEmails { get; set; }

    [Required]
    public string Status { get; set; } = "Pending"; // "Pending", "Processing", "Completed", "Failed", "Stopped"

    public ICollection<VerificationResult> Results { get; set; } = new List<VerificationResult>();

    public ICollection<JobEmail> JobEmails { get; set; } = new List<JobEmail>();
}
