using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SimpleTasks.Messaging;
using SimpleTasks.Contracts;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Domain;

namespace SimpleTasks.Work.Features;

/// <summary>
/// One-time, read-only copy of the original Tasks table into a starter project. This is
/// the only code that reads outside the Work database; the source database is never changed.
/// </summary>
public sealed class LegacyTaskImport(WorkDb db, IConfiguration configuration, ILogger<LegacyTaskImport> logger)
{
    public const string ProjectKey = "ST";

    private sealed record LegacyTask(string Title, bool IsCompleted, DateTime CreatedAt);

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (await db.LegacyImports.AnyAsync(ct))
        {
            return;
        }

        var source = configuration.GetConnectionString("DefaultConnection")!;
        var sourceDatabase = new SqlConnectionStringBuilder(source).InitialCatalog;
        if (string.IsNullOrEmpty(sourceDatabase))
        {
            return;
        }
        var tasks = await ReadTasksAsync(sourceDatabase, source, ct);
        if (tasks is null)
        {
            logger.LogInformation("No original Tasks table in {Database}; nothing to import.", sourceDatabase);
            return;
        }

        var project = new Project
        {
            Key = ProjectKey,
            Name = "Simple Tasks",
            Description = "Tasks imported from the original Simple Tasks app.",
            LastTicketNumber = tasks.Count
        };
        db.Projects.Add(project);

        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var ticket = new Ticket
            {
                Project = project,
                ProjectId = project.Id,
                Number = index + 1,
                Title = task.Title,
                Status = task.IsCompleted ? TicketStatus.Done : TicketStatus.ToDo,
                Rank = (index + 1) * Ranking.Gap,
                CreatedAt = task.CreatedAt,
                UpdatedAt = task.CreatedAt
            };
            db.Tickets.Add(ticket);
            db.Activity.Add(new ActivityEntry { TicketId = ticket.Id, Text = "imported from the original task list", CreatedAt = task.CreatedAt });
            db.Publish(new TicketChanged(ticket.Id, ticket.Version, ticket.Key, ticket.Title, null, null, task.IsCompleted, false));
        }

        db.LegacyImports.Add(new LegacyImport { SourceDatabase = sourceDatabase, TaskCount = tasks.Count });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Count} tasks from {Database} into project {Key}.", tasks.Count, sourceDatabase, ProjectKey);
    }

    private async Task<List<LegacyTask>?> ReadTasksAsync(string sourceDatabase, string source, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT DB_ID(@name)";
            exists.Parameters.AddWithValue("@name", sourceDatabase);
            if (await exists.ExecuteScalarAsync(ct) is null or DBNull)
            {
                return null;
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        await using var reader = new SqlConnection(source);
        await reader.OpenAsync(ct);
        await using var hasTable = reader.CreateCommand();
        hasTable.CommandText = "SELECT OBJECT_ID('dbo.Tasks', 'U')";
        if (await hasTable.ExecuteScalarAsync(ct) is null or DBNull)
        {
            return null;
        }

        await using var select = reader.CreateCommand();
        select.CommandText = "SELECT Title, IsCompleted, CreatedAt FROM dbo.Tasks ORDER BY Id";
        await using var rows = await select.ExecuteReaderAsync(ct);
        var tasks = new List<LegacyTask>();
        while (await rows.ReadAsync(ct))
        {
            tasks.Add(new LegacyTask(rows.GetString(0), rows.GetBoolean(1), DateTime.SpecifyKind(rows.GetDateTime(2), DateTimeKind.Utc)));
        }
        return tasks;
    }
}
