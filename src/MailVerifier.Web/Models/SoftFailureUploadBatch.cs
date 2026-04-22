using System.ComponentModel.DataAnnotations;

namespace MailVerifier.Web.Models;

public class SoftFailureUploadBatch
{
    public int Id { get; set; }

    [MaxLength(256)]
    public string? FileName { get; set; }

    [Required]
    public string UploadedByUser { get; set; } = string.Empty;

    public string? UploadedByName { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public int TotalRows { get; set; }

    public int FailureRowsRecorded { get; set; }

    public int SuccessRowsApplied { get; set; }

    public int RecipientRemovals { get; set; }

    public ICollection<SoftFailureEvent> Events { get; set; } = new List<SoftFailureEvent>();
}
