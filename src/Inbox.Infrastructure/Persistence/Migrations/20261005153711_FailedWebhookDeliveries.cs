using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FailedWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsFailed",
                table: "WebhookEvents",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Everything this column can mean already exists, written by the old code as "finished with
            // an error": a delivery that used up its retries, or whose events were all rejected. Without
            // the backfill those rows would arrive as successes and be aged out on the 14-day schedule,
            // taking the operator's backlog with them - the very rows the longer retention exists for.
            migrationBuilder.Sql(
                """
                UPDATE [WebhookEvents]
                SET [IsFailed] = 1
                WHERE [ProcessedAtUtc] IS NOT NULL
                  AND [LastError] IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_Failed",
                table: "WebhookEvents",
                column: "ProcessedAtUtc",
                filter: "[IsFailed] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookEvents_Failed",
                table: "WebhookEvents");

            migrationBuilder.DropColumn(
                name: "IsFailed",
                table: "WebhookEvents");
        }
    }
}
