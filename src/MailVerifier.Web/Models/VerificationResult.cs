using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MailVerifier.Web.Models;

public class VerificationResult
{
    private static readonly HashSet<string> CommonMailboxNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "abuse",
        "admin",
        "accounts",
        "billing",
        "careers",
        "compliance",
        "contact",
        "customerservice",
        "enquiries",
        "enquiry",
        "hello",
        "help",
        "hr",
        "info",
        "inquiries",
        "inquiry",
        "jobs",
        "mail",
        "marketing",
        "office",
        "postmaster",
        "press",
        "privacy",
        "sales",
        "security",
        "service",
        "support",
        "team",
        "webmaster",
        "whois"
    };

    private static HashSet<string> AdditionalCommonMailboxNames = new(StringComparer.OrdinalIgnoreCase);

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

    public bool IsPotentialSoftFailure { get; set; }

    public string? SoftFailureNote { get; set; }

    [NotMapped]
    public bool IsRetryable =>
        !string.IsNullOrWhiteSpace(ErrorMessage) &&
        ErrorMessage.StartsWith("Temporary SMTP response", StringComparison.OrdinalIgnoreCase);

    [NotMapped]
    public bool IsVerified =>
        DomainExists && HasMxRecords && MailboxExists && string.IsNullOrWhiteSpace(ErrorMessage);

    public static void ConfigureAdditionalCommonMailboxNames(IEnumerable<string>? names)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (names != null)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                normalized.Add(name.Trim());
            }
        }

        AdditionalCommonMailboxNames = normalized;
    }

    [NotMapped]
    public bool IsCommonMailbox
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EmailAddress))
                return false;

            var atIndex = EmailAddress.IndexOf('@');
            if (atIndex <= 0)
                return false;

            var localPart = EmailAddress[..atIndex].Trim();
            return localPart.Length > 0
                && (CommonMailboxNames.Contains(localPart) || AdditionalCommonMailboxNames.Contains(localPart));
        }
    }

    [NotMapped]
    public bool IsAtRisk => IsVerified && IsCommonMailbox;

    [NotMapped]
    public bool IsInvalidMailbox =>
        DomainExists && HasMxRecords && !MailboxExists && string.IsNullOrWhiteSpace(ErrorMessage);
}
