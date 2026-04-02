namespace MailVerifier.Web.Models;

public class JobEmail
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public VerificationJob Job { get; set; } = null!;
    public string EmailAddress { get; set; } = string.Empty;
}
