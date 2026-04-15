using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailVerifier.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadedByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UploadedByName",
                table: "VerificationJobs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadedByName",
                table: "VerificationJobs");
        }
    }
}
