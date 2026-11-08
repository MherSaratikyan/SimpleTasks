using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleTasks.Work.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketMetadataAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AssignedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssignedById",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedAt",
                table: "Tickets",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompletedById",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QaOwnerId",
                table: "Tickets",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FromValue",
                table: "Activity",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Activity",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ToValue",
                table: "Activity",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Activity_Kind_CreatedAt",
                table: "Activity",
                columns: new[] { "Kind", "CreatedAt" });

            // Existing history was plain text. Turn the entries we can recognise into structured ones,
            // so tickets created before this change still appear in history and statistics.
            migrationBuilder.Sql("""
                UPDATE Activity SET Kind = 1, ToValue = '0' WHERE Text = 'created this ticket';
                UPDATE Activity SET Kind = 2, ToValue = CASE Text
                        WHEN 'moved this ticket to To Do' THEN '0'
                        WHEN 'moved this ticket to In Progress' THEN '1'
                        WHEN 'moved this ticket to In Review' THEN '2'
                        WHEN 'moved this ticket to Done' THEN '3' END
                    WHERE Text LIKE 'moved this ticket to %';
                UPDATE Activity SET Kind = 3 WHERE Text IN ('changed the assignee', 'unassigned this ticket');
                UPDATE Activity SET Kind = 5 WHERE Text LIKE 'changed the estimate to %';
                UPDATE Activity SET Kind = 6, ToValue = SUBSTRING(Text, 25, 50) WHERE Text LIKE 'changed the priority to %';
                UPDATE Activity SET Kind = 7, ToValue = SUBSTRING(Text, 21, 50) WHERE Text LIKE 'changed the type to %';
                UPDATE Activity SET Kind = 8 WHERE Text LIKE 'renamed this ticket to %';
                UPDATE Activity SET Kind = 9 WHERE Text IN ('changed the due date', 'removed the due date');
                UPDATE Activity SET Kind = 10 WHERE Text = 'deleted this ticket';
                UPDATE Activity SET Kind = 11 WHERE Text = 'restored this ticket';
                UPDATE Activity SET Kind = 12 WHERE Text LIKE 'logged %';
                """);

            // Each status change's "from" is the status before it: the previous change, or To Do at creation.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT Id, FromValue, LAG(ToValue, 1, '0') OVER (PARTITION BY TicketId ORDER BY CreatedAt, Id) AS PreviousStatus
                    FROM Activity WHERE Kind = 2)
                UPDATE ordered SET FromValue = PreviousStatus;
                """);

            // Done by and Completion time come from the latest move to Done; tickets imported as done
            // have no such move, so their last update is the best record of when they finished.
            migrationBuilder.Sql("""
                UPDATE t SET CompletedAt = COALESCE(d.CreatedAt, t.UpdatedAt), CompletedById = d.ActorId
                FROM Tickets t
                OUTER APPLY (SELECT TOP 1 a.CreatedAt, a.ActorId FROM Activity a
                             WHERE a.TicketId = t.Id AND a.Kind = 2 AND a.ToValue = '3' ORDER BY a.CreatedAt DESC) d
                WHERE t.Status = 3;
                """);

            // Assigned by is the latest assignee change, or the creator when it was assigned at creation.
            migrationBuilder.Sql("""
                UPDATE t SET AssignedAt = COALESCE(c.CreatedAt, t.CreatedAt), AssignedById = COALESCE(c.ActorId, t.ReporterId)
                FROM Tickets t
                OUTER APPLY (SELECT TOP 1 a.CreatedAt, a.ActorId FROM Activity a
                             WHERE a.TicketId = t.Id AND a.Kind = 3 ORDER BY a.CreatedAt DESC) c
                WHERE t.AssigneeId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activity_Kind_CreatedAt",
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "AssignedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssignedById",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CompletedById",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QaOwnerId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "FromValue",
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Activity");

            migrationBuilder.DropColumn(
                name: "ToValue",
                table: "Activity");
        }
    }
}
