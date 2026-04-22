using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftFailureRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VerificationResults_JobId",
                table: "VerificationResults");

            migrationBuilder.DropIndex(
                name: "IX_JobEmails_JobId",
                table: "JobEmails");

            migrationBuilder.AddColumn<bool>(
                name: "IsPotentialSoftFailure",
                table: "VerificationResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SoftFailureNote",
                table: "VerificationResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SoftFailureRecipients",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftFailureRecipients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoftFailureUploadBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    UploadedByUser = table.Column<string>(type: "TEXT", nullable: false),
                    UploadedByName = table.Column<string>(type: "TEXT", nullable: true),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalRows = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureRowsRecorded = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessRowsApplied = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientRemovals = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftFailureUploadBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SoftFailureEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecipientId = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadBatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Response = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_EmailAddress",
                table: "VerificationResults",
                column: "EmailAddress");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_JobId_EmailAddress",
                table: "VerificationResults",
                columns: new[] { "JobId", "EmailAddress" },
                unique: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SoftFailureEvents");

            migrationBuilder.DropTable(
                name: "SoftFailureRecipients");

            migrationBuilder.DropTable(
                name: "SoftFailureUploadBatches");

            migrationBuilder.DropIndex(
                name: "IX_VerificationResults_EmailAddress",
                table: "VerificationResults");

            migrationBuilder.DropIndex(
                name: "IX_VerificationResults_JobId_EmailAddress",
                table: "VerificationResults");

            migrationBuilder.DropIndex(
                name: "IX_JobEmails_JobId_EmailAddress",
                table: "JobEmails");

            migrationBuilder.DropColumn(
                name: "IsPotentialSoftFailure",
                table: "VerificationResults");

            migrationBuilder.DropColumn(
                name: "SoftFailureNote",
                table: "VerificationResults");

            migrationBuilder.CreateIndex(
                name: "IX_VerificationResults_JobId",
                table: "VerificationResults",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobEmails_JobId",
                table: "JobEmails",
                column: "JobId");
        }
    }
}
