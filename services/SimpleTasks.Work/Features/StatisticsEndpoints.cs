using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.ServiceDefaults;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Domain;

namespace SimpleTasks.Work.Features;

public sealed record PersonRow(Guid UserId, string Name, int Completed, int OpenAssigned);
public sealed record ProjectRow(string Key, string Name, int Opened, int Closed, int Open);
public sealed record StatisticsResponse(
    DateOnly From, DateOnly To, string? ProjectKey,
    int Opened, int Closed, int Reopened, int CurrentlyOpen, double? AverageHoursToComplete,
    IReadOnlyList<DayStats> Days, IReadOnlyList<StatusTotal> CurrentByStatus,
    IReadOnlyList<PersonRow> People, IReadOnlyList<ProjectRow> Projects);

public static class StatisticsEndpoints
{
    public static readonly int[] AllowedDays = [7, 30, 90];

    public static void MapStatistics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/statistics", async (WorkDb db, CancellationToken ct, int days = 30, string? project = null, string? tz = null) =>
        {
            if (!AllowedDays.Contains(days))
            {
                return Results.BadRequest(new { error = "Choose 7, 30 or 90 days." });
            }
            var zone = tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out var found) ? found : TimeZoneInfo.Utc;

            Guid? projectId = null;
            if (!string.IsNullOrWhiteSpace(project))
            {
                projectId = await db.Projects.Where(item => item.Key == project.ToUpper()).Select(item => (Guid?)item.Id).FirstOrDefaultAsync(ct);
                if (projectId is null)
                {
                    return Results.NotFound();
                }
            }

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var firstDay = today.AddDays(1 - days);
            // A day's margin either side covers any time zone; the calculator trims to exact local days.
            var since = firstDay.ToDateTime(TimeOnly.MinValue).AddDays(-1);

            var tickets = db.Tickets.AsNoTracking().Where(ticket => ticket.DeletedAt == null);
            if (projectId is not null)
            {
                tickets = tickets.Where(ticket => ticket.ProjectId == projectId);
            }

            var ticketRows = await tickets
                .Select(ticket => new StatsTicket(ticket.Id, ticket.ProjectId, ticket.CreatedAt, ticket.CompletedAt, ticket.Status, ticket.AssigneeId))
                .ToListAsync(ct);
            var changeRows = await (
                from entry in db.Activity.AsNoTracking()
                join ticket in tickets on entry.TicketId equals ticket.Id
                where entry.Kind == HistoryKind.Status && entry.CreatedAt >= since
                select new { entry.TicketId, entry.ActorId, entry.CreatedAt, entry.FromValue, entry.ToValue })
                .ToListAsync(ct);
            var changes = changeRows
                .Where(row => int.TryParse(row.ToValue, out _))
                .Select(row => new StatsStatusChange(row.TicketId, row.ActorId, row.CreatedAt,
                    int.TryParse(row.FromValue, out var from) ? (TicketStatus)from : null, (TicketStatus)int.Parse(row.ToValue!)))
                .ToList();

            var stats = Statistics.Calculate(ticketRows, changes, firstDay, today, zone);

            var userIds = stats.People.Select(person => person.UserId).ToList();
            var names = await db.Users.AsNoTracking().Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);
            var projectIds = stats.Projects.Select(row => row.ProjectId).ToList();
            var projects = await db.Projects.AsNoTracking().Where(item => projectIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, item => (item.Key, item.Name), ct);

            return Results.Ok(new StatisticsResponse(
                firstDay, today, projectId is null ? null : project!.ToUpper(),
                stats.Opened, stats.Closed, stats.Reopened, stats.CurrentlyOpen, stats.AverageHoursToComplete,
                stats.Days, stats.CurrentByStatus,
                stats.People.Select(person => new PersonRow(person.UserId, names.GetValueOrDefault(person.UserId, "Unknown user"),
                    person.Completed, person.OpenAssigned)).ToList(),
                stats.Projects.Where(row => projects.ContainsKey(row.ProjectId))
                    .Select(row => new ProjectRow(projects[row.ProjectId].Key, projects[row.ProjectId].Name, row.Opened, row.Closed, row.Open))
                    .OrderBy(row => row.Name).ToList()));
        }).RequireAuthorization(policy => policy.RequireClaim(Jwt.SiteAdminClaim, "true"));
    }
}
