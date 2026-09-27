using Microsoft.EntityFrameworkCore;
using MailVerifier.Web.Data;
using MailVerifier.Web.Security;

namespace MailVerifier.Web.Endpoints;

public static class JobApiEndpoints
{
    // Rows are re-sent for this long after the cursor so a write that commits late is never skipped;
    // the client upserts by id, so repeats are harmless.
    private static readonly TimeSpan CursorOverlap = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapJobApi(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs/{id:int}").RequireAuthorization();

        // Live progress, polled by the job details page. Returns only results changed since `since`
        // (unix ms, from the previous response's cursor) and never the SMTP logs.
        jobs.MapGet("/progress", async (int id, long? since, AppDbContext db, HttpContext ctx) =>
        {
            var job = await db.VerificationJobs
                .AsNoTracking()
                .AccessibleTo(ctx.User)
                .Where(j => j.Id == id)
                .Select(j => new { j.Status, j.TotalEmails, j.ProcessedEmails, j.CreatedAt })
                .FirstOrDefaultAsync();

            if (job == null)
                return Results.NotFound();

            var results = db.VerificationResults.AsNoTracking().Where(r => r.JobId == id);
            var validCount = await results.CountAsync(r => r.MailboxExists);

            if (since is > 0)
            {
                var sinceUtc = DateTimeOffset.FromUnixTimeMilliseconds(since.Value).UtcDateTime - CursorOverlap;
                results = results.Where(r => r.VerifiedAt > sinceUtc);
            }

            var changed = await results.OrderBy(r => r.EmailAddress).SelectSummaries().ToListAsync();
            var cursor = changed.Count > 0
                ? changed.Max(s => ToUnixMs(s.Result.VerifiedAt))
                : since ?? 0;

            return Results.Json(new
            {
                job.Status,
                job.TotalEmails,
                job.ProcessedEmails,
                CreatedAtUtc = ToUnixMs(job.CreatedAt),
                ValidCount = validCount,
                Cursor = cursor,
                Results = changed.Select(s => new
                {
                    s.Result.Id,
                    s.Result.EmailAddress,
                    s.Result.DomainExists,
                    s.Result.HasMxRecords,
                    s.Result.MailboxExists,
                    s.Result.IsCommonMailbox,
                    s.Result.IsCatchAll,
                    s.Result.IsAtRisk,
                    s.Result.IsPotentialSoftFailure,
                    s.Result.SoftFailureNote,
                    s.Result.IsRetryable,
                    s.Result.IsRetested,
                    s.Result.PendingRetest,
                    s.Result.IsVerified,
                    s.Result.ErrorMessage,
                    s.HasLog,
                    VerifiedAt = s.Result.VerifiedAt.ToLocalTime().ToString("HH:mm:ss")
                })
            });
        });

        jobs.MapGet("/results/{resultId:int}/log", async (int id, int resultId, AppDbContext db, HttpContext ctx) =>
        {
            var hasAccess = await db.VerificationJobs.AccessibleTo(ctx.User).AnyAsync(j => j.Id == id);
            if (!hasAccess)
                return Results.NotFound();

            var log = await db.VerificationResults
                .AsNoTracking()
                .Where(r => r.Id == resultId && r.JobId == id)
                .Select(r => r.SmtpLog)
                .FirstOrDefaultAsync();

            return log == null ? Results.NotFound() : Results.Text(log, "text/plain");
        });

        return app;
    }

    private static long ToUnixMs(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
