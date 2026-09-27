using System.Security.Claims;
using MailVerifier.Web.Models;

namespace MailVerifier.Web.Security;

public static class JobAccess
{
    /// <summary>Admins see every job; everyone else sees only the jobs they uploaded.</summary>
    public static IQueryable<VerificationJob> AccessibleTo(this IQueryable<VerificationJob> jobs, ClaimsPrincipal user)
    {
        if (UserAccess.IsAdmin(user))
            return jobs;

        var userId = UserAccess.GetUserId(user);
        return string.IsNullOrWhiteSpace(userId)
            ? jobs.Where(_ => false)
            : jobs.Where(j => j.UploadedByUser == userId);
    }
}
