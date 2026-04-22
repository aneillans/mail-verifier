using System.ComponentModel.DataAnnotations;

namespace MailVerifier.Web.Models;

public class SoftFailureRecipient
{
    public int Id { get; set; }

    [Required]
    [MaxLength(320)]
    public string EmailAddress { get; set; } = string.Empty;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public ICollection<SoftFailureEvent> Events { get; set; } = new List<SoftFailureEvent>();
}
