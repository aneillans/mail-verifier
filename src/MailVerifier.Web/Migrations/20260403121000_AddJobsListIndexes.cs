using MailVerifier.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260403121000_AddJobsListIndexes")]
    public partial class AddJobsListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VerificationJobs_CreatedAt",
                table: "VerificationJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationJobs_UploadedByUser_CreatedAt",
                table: "VerificationJobs",
                columns: new[] { "UploadedByUser", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationJobs_CreatedAt",
                table: "VerificationJobs");

            migrationBuilder.DropIndex(
                name: "IX_VerificationJobs_UploadedByUser_CreatedAt",
                table: "VerificationJobs");
        }
    }
}
