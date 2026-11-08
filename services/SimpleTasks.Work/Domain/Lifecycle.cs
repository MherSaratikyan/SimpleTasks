using SimpleTasks.Contracts;
using SimpleTasks.Work.Data;

namespace SimpleTasks.Work.Domain;

/// <summary>What a history entry records. Stored as a number, so only ever add to the end.</summary>
public enum HistoryKind
{
    Note = 0,
    Created = 1,
    Status = 2,
    Assignee = 3,
    QaOwner = 4,
    Estimate = 5,
    Priority = 6,
    Type = 7,
    Title = 8,
    DueDate = 9,
    Deleted = 10,
    Restored = 11,
    WorkLogged = 12
}

public static class TicketLifecycle
{
    /// <summary>
    /// Moves a ticket to a status. Finishing records who and when; reopening clears both,
    /// so they always describe the latest completion. The history keeps the earlier ones.
    /// </summary>
    public static void ApplyStatus(Ticket ticket, TicketStatus status, Guid actor, DateTime now)
    {
        if (status == ticket.Status)
        {
            return;
        }
        if (status == TicketStatus.Done)
        {
            ticket.CompletedAt = now;
            ticket.CompletedById = actor;
        }
        else if (ticket.Status == TicketStatus.Done)
        {
            ticket.CompletedAt = null;
            ticket.CompletedById = null;
        }
        ticket.Status = status;
    }

    public static void ApplyAssignee(Ticket ticket, Guid? assignee, Guid actor, DateTime now)
    {
        if (assignee == ticket.AssigneeId)
        {
            return;
        }
        ticket.AssigneeId = assignee;
        ticket.AssignedById = assignee is null ? null : actor;
        ticket.AssignedAt = assignee is null ? null : now;
    }

    /// <summary>The QA owner is asked to review when a ticket enters In Review, or is given one while there.</summary>
    public static bool ShouldRequestReview(TicketStatus before, TicketStatus after, Guid? qaBefore, Guid? qaAfter) =>
        qaAfter is not null && after == TicketStatus.InReview && (before != TicketStatus.InReview || qaBefore != qaAfter);
}

public sealed record StatsTicket(
    Guid Id, Guid ProjectId, DateTime CreatedAt, DateTime? CompletedAt, TicketStatus Status, Guid? AssigneeId);

public sealed record StatsStatusChange(Guid TicketId, Guid? ActorId, DateTime At, TicketStatus? From, TicketStatus To);

public sealed record DayStats(DateOnly Date, int Opened, int Closed, int ToDo, int InProgress, int InReview, int Done);
public sealed record PersonStats(Guid UserId, int Completed, int OpenAssigned);
public sealed record ProjectStats(Guid ProjectId, int Opened, int Closed, int Open);
public sealed record StatusTotal(TicketStatus Status, int Count);

public sealed record StatsResult(
    int Opened, int Closed, int Reopened, int CurrentlyOpen, double? AverageHoursToComplete,
    IReadOnlyList<DayStats> Days, IReadOnlyList<StatusTotal> CurrentByStatus,
    IReadOnlyList<PersonStats> People, IReadOnlyList<ProjectStats> Projects);

/// <summary>Turns tickets and their status changes into dashboard figures for days in the viewer's time zone.</summary>
public static class Statistics
{
    public static StatsResult Calculate(
        IReadOnlyList<StatsTicket> tickets, IReadOnlyList<StatsStatusChange> changes,
        DateOnly firstDay, DateOnly lastDay, TimeZoneInfo zone)
    {
        DateOnly Day(DateTime utc) =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
        bool InRange(DateTime utc) => Day(utc) is var day && day >= firstDay && day <= lastDay;

        var opened = tickets.Where(ticket => InRange(ticket.CreatedAt)).ToList();
        var moves = changes.Where(change => InRange(change.At)).ToList();
        var closes = moves.Where(change => change.To == TicketStatus.Done).ToList();
        var reopens = moves.Count(change => change.From == TicketStatus.Done && change.To != TicketStatus.Done);

        var finished = tickets.Where(ticket => ticket.CompletedAt is { } done && InRange(done)).ToList();
        double? averageHours = finished.Count == 0
            ? null
            : Math.Round(finished.Average(ticket => (ticket.CompletedAt!.Value - ticket.CreatedAt).TotalHours), 1);

        var days = new List<DayStats>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var dayMoves = moves.Where(change => Day(change.At) == day).ToList();
            days.Add(new DayStats(
                day,
                opened.Count(ticket => Day(ticket.CreatedAt) == day),
                dayMoves.Count(change => change.To == TicketStatus.Done),
                dayMoves.Count(change => change.To == TicketStatus.ToDo),
                dayMoves.Count(change => change.To == TicketStatus.InProgress),
                dayMoves.Count(change => change.To == TicketStatus.InReview),
                dayMoves.Count(change => change.To == TicketStatus.Done)));
        }

        var open = tickets.Where(ticket => ticket.Status != TicketStatus.Done).ToList();
        var byStatus = Enum.GetValues<TicketStatus>()
            .Select(status => new StatusTotal(status, tickets.Count(ticket => ticket.Status == status)))
            .ToList();

        var completedBy = closes.Where(change => change.ActorId is not null)
            .GroupBy(change => change.ActorId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var assignedTo = open.Where(ticket => ticket.AssigneeId is not null)
            .GroupBy(ticket => ticket.AssigneeId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var people = completedBy.Keys.Union(assignedTo.Keys)
            .Select(id => new PersonStats(id, completedBy.GetValueOrDefault(id), assignedTo.GetValueOrDefault(id)))
            .OrderByDescending(person => person.Completed).ThenByDescending(person => person.OpenAssigned)
            .ToList();

        var ticketProject = tickets.ToDictionary(ticket => ticket.Id, ticket => ticket.ProjectId);
        var projects = tickets.Select(ticket => ticket.ProjectId).Distinct()
            .Select(projectId => new ProjectStats(
                projectId,
                opened.Count(ticket => ticket.ProjectId == projectId),
                closes.Count(change => ticketProject.GetValueOrDefault(change.TicketId) == projectId),
                open.Count(ticket => ticket.ProjectId == projectId)))
            .ToList();

        return new StatsResult(opened.Count, closes.Count, reopens, open.Count, averageHours, days, byStatus, people, projects);
    }
}
