namespace MailVerifier.Web.Models;

public static class JobStatus
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Stopped = "Stopped";

    public static bool IsActive(string? status) => status is Pending or Processing;
}
