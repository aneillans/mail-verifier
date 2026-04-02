using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MailVerifier.Web.Models;

public class VerificationResult
{
    public int Id { get; set; }

    public int JobId { get; set; }

    public VerificationJob Job { get; set; } = null!;

    [Required]
    public string EmailAddress { get; set; } = string.Empty;

    public bool DomainExists { get; set; }

    public bool HasMxRecords { get; set; }

    public bool MailboxExists { get; set; }

    // Original test results (if this is a retest)
    public bool? OriginalDomainExists { get; set; }

    public bool? OriginalHasMxRecords { get; set; }

    public bool? OriginalMailboxExists { get; set; }

    public string SmtpLog { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;

    // Track first test date and whether this has been retested
    public DateTime? FirstTestedAt { get; set; }

    public bool IsRetested { get; set; } = false;

    [NotMapped]
    public bool IsRetryable =>
        !string.IsNullOrWhiteSpace(ErrorMessage) &&
        ErrorMessage.StartsWith("Temporary SMTP response", StringComparison.OrdinalIgnoreCase);

    [NotMapped]
    public bool IsVerified =>
        DomainExists && HasMxRecords && MailboxExists && string.IsNullOrWhiteSpace(ErrorMessage);

    [NotMapped]
    public bool IsInvalidMailbox =>
        DomainExists && HasMxRecords && !MailboxExists && string.IsNullOrWhiteSpace(ErrorMessage);
}
