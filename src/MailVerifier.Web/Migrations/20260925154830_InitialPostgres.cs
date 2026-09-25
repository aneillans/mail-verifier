using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SoftFailureRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmailAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftFailureRecipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoftFailureUploadBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UploadedByUser = table.Column<string>(type: "text", nullable: false),
                    UploadedByName = table.Column<string>(type: "text", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    FailureRowsRecorded = table.Column<int>(type: "integer", nullable: false),
                    SuccessRowsApplied = table.Column<int>(type: "integer", nullable: false),
                    RecipientRemovals = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftFailureUploadBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerificationJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: true),
                    UploadedByUser = table.Column<string>(type: "text", nullable: false),
                    UploadedByName = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TotalEmails = table.Column<int>(type: "integer", nullable: false),
                    ProcessedEmails = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoftFailureEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RecipientId = table.Column<int>(type: "integer", nullable: false),
                    UploadBatchId = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Response = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftFailureEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftFailureEvents_SoftFailureRecipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "SoftFailureRecipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SoftFailureEvents_SoftFailureUploadBatches_UploadBatchId",
                        column: x => x.UploadBatchId,
                        principalTable: "SoftFailureUploadBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobEmails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    EmailAddress = table.Column<string>(type: "text", nullable: false)
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
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    EmailAddress = table.Column<string>(type: "text", nullable: false),
                    DomainExists = table.Column<bool>(type: "boolean", nullable: false),
                    HasMxRecords = table.Column<bool>(type: "boolean", nullable: false),
                    MailboxExists = table.Column<bool>(type: "boolean", nullable: false),
                    OriginalDomainExists = table.Column<bool>(type: "boolean", nullable: true),
                    OriginalHasMxRecords = table.Column<bool>(type: "boolean", nullable: true),
                    OriginalMailboxExists = table.Column<bool>(type: "boolean", nullable: true),
                    SmtpLog = table.Column<string>(type: "text", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FirstTestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsRetested = table.Column<bool>(type: "boolean", nullable: false),
                    IsPotentialSoftFailure = table.Column<bool>(type: "boolean", nullable: false),
                    SoftFailureNote = table.Column<string>(type: "text", nullable: true)
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
                name: "IX_JobEmails_JobId_EmailAddress",
                table: "JobEmails",
                columns: new[] { "JobId", "EmailAddress" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureEvents_RecipientId",
                table: "SoftFailureEvents",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureEvents_RecipientId_RecordedAt",
                table: "SoftFailureEvents",
                columns: new[] { "RecipientId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureEvents_RecordedAt",
                table: "SoftFailureEvents",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureEvents_UploadBatchId",
                table: "SoftFailureEvents",
                column: "UploadBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureRecipients_EmailAddress",
                table: "SoftFailureRecipients",
                column: "EmailAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureRecipients_LastSeenAt",
                table: "SoftFailureRecipients",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_SoftFailureUploadBatches_UploadedAt",
                table: "SoftFailureUploadBatches",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationJobs_CreatedAt",
                table: "VerificationJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationJobs_UploadedByUser_CreatedAt",
                table: "VerificationJobs",
                columns: new[] { "UploadedByUser", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_EmailAddress",
                table: "VerificationResults",
                column: "EmailAddress");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_JobId_EmailAddress",
                table: "VerificationResults",
                columns: new[] { "JobId", "EmailAddress" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobEmails");

            migrationBuilder.DropTable(
                name: "SoftFailureEvents");

            migrationBuilder.DropTable(
                name: "VerificationResults");

            migrationBuilder.DropTable(
                name: "SoftFailureRecipients");

            migrationBuilder.DropTable(
                name: "SoftFailureUploadBatches");

            migrationBuilder.DropTable(
                name: "VerificationJobs");
        }
    }
}
