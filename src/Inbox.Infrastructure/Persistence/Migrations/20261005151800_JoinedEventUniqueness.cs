using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inbox.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class JoinedEventUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The only migration here that can fail on a database that has actually been used: the
            // "recorded once" check was an application-level pre-check until now, so a database that ran
            // under it can already hold two join rows for the same agent on the same thread. Keep the
            // oldest of each pair - the one the code that lost the race meant to leave alone - and drop
            // the rest, because a unique index cannot be built over them.
            //
            // Not reversible, and deliberately so: a duplicate join row is the bug this migration fixes,
            // and putting it back would only reintroduce the index failure on the way down.
            migrationBuilder.Sql(
                """
                DELETE FROM [ConversationEvents]
                WHERE [Type] = N'Joined'
                  AND [ActorAgentId] IS NOT NULL
                  AND [Id] NOT IN (
                      SELECT [Id]
                      FROM (
                          SELECT [Id],
                                 ROW_NUMBER() OVER (
                                     PARTITION BY [ConversationId], [ActorAgentId]
                                     ORDER BY [CreatedAtUtc], [Id]) AS [rn]
                          FROM [ConversationEvents]
                          WHERE [Type] = N'Joined' AND [ActorAgentId] IS NOT NULL
                      ) AS [ranked]
                      WHERE [ranked].[rn] = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEvents_ConversationId_ActorAgentId_Joined",
                table: "ConversationEvents",
                columns: new[] { "ConversationId", "ActorAgentId" },
                unique: true,
                filter: "[Type] = N'Joined' AND [ActorAgentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationEvents_ConversationId_ActorAgentId_Joined",
                table: "ConversationEvents");
        }
    }
}
