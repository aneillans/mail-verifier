using MailVerifier.Web.Models;

namespace MailVerifier.Web.Data;

public sealed class ResultSummary
{
    public required VerificationResult Result { get; init; }
    public bool HasLog { get; init; }
}

public static class ResultQueries
{
    /// <summary>
    /// Projects results without the (large) SMTP log, which is fetched on demand instead.
    /// The returned entities are not tracked.
    /// </summary>
    public static IQueryable<ResultSummary> SelectSummaries(this IQueryable<VerificationResult> results) =>
        results.Select(r => new ResultSummary
        {
            HasLog = r.SmtpLog != "",
            Result = new VerificationResult
            {
                Id = r.Id,
                JobId = r.JobId,
                EmailAddress = r.EmailAddress,
                DomainExists = r.DomainExists,
                HasMxRecords = r.HasMxRecords,
                MailboxExists = r.MailboxExists,
                OriginalDomainExists = r.OriginalDomainExists,
                OriginalHasMxRecords = r.OriginalHasMxRecords,
                OriginalMailboxExists = r.OriginalMailboxExists,
                ErrorMessage = r.ErrorMessage,
                VerifiedAt = r.VerifiedAt,
                FirstTestedAt = r.FirstTestedAt,
                IsRetested = r.IsRetested,
                IsPotentialSoftFailure = r.IsPotentialSoftFailure,
                SoftFailureNote = r.SoftFailureNote,
                IsCatchAll = r.IsCatchAll,
                PendingRetest = r.PendingRetest
            }
        });
}
