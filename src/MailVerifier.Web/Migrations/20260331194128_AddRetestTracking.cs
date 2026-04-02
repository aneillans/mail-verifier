using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddRetestTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VerificationJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UploadedByUser = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalEmails = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessedEmails = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobEmails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobEmails_VerificationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "VerificationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VerificationResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", nullable: false),
                    DomainExists = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasMxRecords = table.Column<bool>(type: "INTEGER", nullable: false),
                    MailboxExists = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginalDomainExists = table.Column<bool>(type: "INTEGER", nullable: true),
                    OriginalHasMxRecords = table.Column<bool>(type: "INTEGER", nullable: true),
                    OriginalMailboxExists = table.Column<bool>(type: "INTEGER", nullable: true),
                    SmtpLog = table.Column<string>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirstTestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRetested = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificationResults_VerificationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "VerificationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobEmails_JobId",
                table: "JobEmails",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_JobId",
                table: "VerificationResults",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobEmails");

            migrationBuilder.DropTable(
                name: "VerificationResults");

            migrationBuilder.DropTable(
                name: "VerificationJobs");
        }
    }
}
