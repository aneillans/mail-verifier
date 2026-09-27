using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    public partial class RetestQueueCatchAllAndLowercaseEmails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCatchAll",
                table: "VerificationResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PendingRetest",
                table: "VerificationResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_JobId_VerifiedAt",
                table: "VerificationResults",
                columns: new[] { "JobId", "VerifiedAt" });

            // Postgres compares text case-sensitively; addresses are now stored lower-cased.
            // Drop case-variant duplicates within a job first so the unique (JobId, EmailAddress) index holds.
            migrationBuilder.Sql("""
                DELETE FROM "VerificationResults" a
                USING "VerificationResults" b
                WHERE a."JobId" = b."JobId"
                  AND lower(btrim(a."EmailAddress")) = lower(btrim(b."EmailAddress"))
                  AND a."Id" > b."Id";
                """);
            migrationBuilder.Sql("""
                UPDATE "VerificationResults" SET "EmailAddress" = lower(btrim("EmailAddress"))
                WHERE "EmailAddress" <> lower(btrim("EmailAddress"));
                """);
            migrationBuilder.Sql("""
                UPDATE "JobEmails" SET "EmailAddress" = lower(btrim("EmailAddress"))
                WHERE "EmailAddress" <> lower(btrim("EmailAddress"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationResults_JobId_VerifiedAt",
                table: "VerificationResults");

            migrationBuilder.DropColumn(
                name: "IsCatchAll",
                table: "VerificationResults");

            migrationBuilder.DropColumn(
                name: "PendingRetest",
                table: "VerificationResults");
        }
    }
}
