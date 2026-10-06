using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6MediaAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded version, which defaulted the column to "". Enums are
            // persisted as strings and read back through a converter, so an empty string would throw on
            // the first row that had one. Every row written before this column existed was a file that
            // had been stored - StoragePath was mandatory and nothing wrote a placeholder - so
            // "Available" is what those rows already meant.
            migrationBuilder.AddColumn<string>(
                name: "Availability",
                table: "MessageAttachments",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddColumn<string>(
                name: "UnavailableReason",
                table: "MessageAttachments",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Availability",
                table: "MessageAttachments");

            migrationBuilder.DropColumn(
                name: "UnavailableReason",
                table: "MessageAttachments");
        }
    }
}
