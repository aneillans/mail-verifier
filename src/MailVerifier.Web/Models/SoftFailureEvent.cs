using System.ComponentModel.DataAnnotations;

namespace MailVerifier.Web.Models;

public class SoftFailureEvent
{
    public int Id { get; set; }

    public int RecipientId { get; set; }

    public SoftFailureRecipient Recipient { get; set; } = null!;

    public int UploadBatchId { get; set; }

    public SoftFailureUploadBatch UploadBatch { get; set; } = null!;

    [MaxLength(128)]
    public string? ErrorCode { get; set; }

    [MaxLength(2048)]
    public string? Response { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
