using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Domain;

namespace SimpleTasks.Work.Features;

public sealed record UserRef(Guid Id, string DisplayName, string? AvatarUrl);
public sealed record TicketCard(
    string Key, string Title, TicketType Type, TicketStatus Status, TicketPriority Priority, UserRef? Assignee,
    int? EstimateMinutes, int LoggedMinutes, string? Estimate, string Logged, DateTime? DueAt, double Rank,
    IReadOnlyList<LabelView> Labels, DateTime? DeletedAt, UserRef? QaOwner);
public sealed record CommentView(Guid Id, UserRef? Author, string Body, DateTime CreatedAt);
public sealed record WorkLogView(Guid Id, UserRef? User, string Time, string? Note, DateTime CreatedAt);

/// <summary>One history entry. From and To are ready to show; they're null when the entry has no before and after.</summary>
public sealed record ActivityView(UserRef? Actor, string Text, DateTime CreatedAt, HistoryKind Kind, string? From, string? To);
public sealed record TicketDetail(
    TicketCard Card, string? Description, UserRef? Reporter, DateTime CreatedAt, DateTime UpdatedAt, ProjectRole Role,
    string ProjectKey, string ProjectName,
    IReadOnlyList<CommentView> Comments, IReadOnlyList<WorkLogView> WorkLogs, IReadOnlyList<ActivityView> Activity,
    UserRef? AssignedBy, DateTime? AssignedAt, UserRef? CompletedBy, DateTime? CompletedAt);
public sealed record CreateTicketRequest(
    string Title, string? Description, TicketType Type, TicketPriority Priority, Guid? AssigneeId,
    string? Estimate, DateTime? DueAt, IReadOnlyList<Guid>? LabelIds, Guid? QaOwnerId = null);
public sealed record UpdateTicketRequest(
    string Title, string? Description, TicketType Type, TicketPriority Priority, Guid? AssigneeId,
    string? Estimate, DateTime? DueAt, IReadOnlyList<Guid>? LabelIds, Guid? QaOwnerId = null);
public sealed record MoveTicketRequest(TicketStatus Status, string? AfterKey, string? BeforeKey);
public sealed record AddCommentRequest(string Body);
public sealed record LogWorkRequest(string Time, string? Note);

public static class Tickets
{
    public static void MapTickets(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects/{projectKey}/tickets").RequireAuthorization();
        var tickets = app.MapGroup("/api/tickets").RequireAuthorization();

        projects.MapGet("/", ListAsync);
        projects.MapPost("/", CreateAsync);
        tickets.MapGet("/mine", MineAsync);
        tickets.MapGet("/{key}", GetAsync);
        tickets.MapPut("/{key}", UpdateAsync);
        tickets.MapPost("/{key}/move", MoveAsync);
        tickets.MapPost("/{key}/comments", CommentAsync);
        tickets.MapPost("/{key}/work-logs", LogWorkAsync);
        tickets.MapDelete("/{key}", DeleteAsync);
        tickets.MapPost("/{key}/restore", RestoreAsync);
    }

    private static async Task<IResult> ListAsync(
        string projectKey, ProjectGate gate, WorkDb db, CancellationToken ct,
        string? q = null, Guid? assignee = null, Guid? label = null, TicketType? type = null,
        TicketPriority? priority = null, bool deleted = false)
    {
        var (project, role) = await gate.OpenAsync(projectKey, ct);
        if (project is null || role is null)
        {
            return Results.NotFound();
        }
        if (deleted && !ProjectAccess.CanAdminister(role))
        {
            return Results.Forbid();
        }

        var query = db.Tickets.AsNoTracking().Where(ticket => ticket.ProjectId == project.Id && (ticket.DeletedAt != null) == deleted);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(ticket => ticket.Title.Contains(q) || (ticket.Description != null && ticket.Description.Contains(q)));
        }
        if (assignee is not null)
        {
            query = query.Where(ticket => ticket.AssigneeId == assignee);
        }
        if (label is not null)
        {
            query = query.Where(ticket => ticket.Labels.Any(item => item.Id == label));
        }
        if (type is not null)
        {
            query = query.Where(ticket => ticket.Type == type);
        }
        if (priority is not null)
        {
            query = query.Where(ticket => ticket.Priority == priority);
        }

        var items = await query.Include(ticket => ticket.Project).Include(ticket => ticket.Labels)
            .OrderBy(ticket => ticket.Rank).ToListAsync(ct);
        var users = await LoadUsersAsync(db, items.SelectMany(ticket => new[] { ticket.AssigneeId, ticket.QaOwnerId }), ct);
        return Results.Ok(items.Select(ticket => ToCard(ticket, users)));
    }

    private static async Task<IResult> MineAsync(HttpContext http, WorkDb db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var items = await db.Tickets.AsNoTracking()
            .Include(ticket => ticket.Project).Include(ticket => ticket.Labels)
            .Where(ticket => ticket.AssigneeId == userId && ticket.DeletedAt == null && ticket.Status != TicketStatus.Done
                && (http.User.IsSiteAdmin() || db.ProjectMembers.Any(member => member.ProjectId == ticket.ProjectId && member.UserId == userId)))
            .OrderBy(ticket => ticket.DueAt == null).ThenBy(ticket => ticket.DueAt).ThenByDescending(ticket => ticket.Priority)
            .Take(100).ToListAsync(ct);
        var users = await LoadUsersAsync(db, [userId], ct);
        return Results.Ok(items.Select(ticket => ToCard(ticket, users)));
    }

    private static async Task<IResult> GetAsync(string key, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var ticket = await FindAsync(db, key, ct, details: true, includeDeleted: true);
        if (ticket is null)
        {
            return Results.NotFound();
        }
        var role = await gate.RoleForAsync(ticket.ProjectId, ct);
        if (role is null || (ticket.DeletedAt is not null && !ProjectAccess.CanAdminister(role)))
        {
            return Results.NotFound();
        }

        var historyUsers = ticket.Activity
            .Where(entry => entry.Kind is HistoryKind.Assignee or HistoryKind.QaOwner)
            .SelectMany(entry => new[] { entry.FromValue, entry.ToValue })
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null);
        var users = await LoadUsersAsync(db, ticket.Comments.Select(comment => (Guid?)comment.AuthorId)
            .Concat(ticket.WorkLogs.Select(log => (Guid?)log.UserId))
            .Concat(ticket.Activity.Select(entry => entry.ActorId))
            .Concat(historyUsers)
            .Append(ticket.AssigneeId).Append(ticket.ReporterId).Append(ticket.QaOwnerId)
            .Append(ticket.AssignedById).Append(ticket.CompletedById), ct);
        return Results.Ok(new TicketDetail(
            ToCard(ticket, users), ticket.Description, Ref(users, ticket.ReporterId), ticket.CreatedAt, ticket.UpdatedAt,
            role.Value, ticket.Project.Key, ticket.Project.Name,
            ticket.Comments.OrderBy(comment => comment.CreatedAt)
                .Select(comment => new CommentView(comment.Id, Ref(users, comment.AuthorId), comment.Body, comment.CreatedAt)).ToList(),
            ticket.WorkLogs.OrderByDescending(log => log.CreatedAt)
                .Select(log => new WorkLogView(log.Id, Ref(users, log.UserId), JiraDuration.Format(log.Minutes), log.Note, log.CreatedAt)).ToList(),
            ticket.Activity.OrderByDescending(entry => entry.CreatedAt)
                .Select(entry => new ActivityView(Ref(users, entry.ActorId), entry.Text, entry.CreatedAt, entry.Kind,
                    Display(entry.Kind, entry.FromValue, users), Display(entry.Kind, entry.ToValue, users))).ToList(),
            Ref(users, ticket.AssignedById), ticket.AssignedAt, Ref(users, ticket.CompletedById), ticket.CompletedAt));
    }

    /// <summary>History stores ids and numbers so names stay current; this turns them into words.</summary>
    private static string? Display(HistoryKind kind, string? value, Dictionary<Guid, UserCopy> users) => kind switch
    {
        HistoryKind.Status => int.TryParse(value, out var status) ? Name((TicketStatus)status) : null,
        HistoryKind.Assignee or HistoryKind.QaOwner => value is null
            ? (kind == HistoryKind.Assignee ? "Unassigned" : "None")
            : Guid.TryParse(value, out var id) && users.TryGetValue(id, out var user) ? user.DisplayName : "Unknown user",
        HistoryKind.Estimate => value is null ? "None" : int.TryParse(value, out var minutes) ? JiraDuration.Format(minutes) : value,
        HistoryKind.Priority or HistoryKind.Type or HistoryKind.Title => value,
        _ => null
    };

    private static void Record(WorkDb db, Ticket ticket, Guid? actor, HistoryKind kind, string text, string? from = null, string? to = null) =>
        db.Activity.Add(new ActivityEntry { TicketId = ticket.Id, ActorId = actor, Kind = kind, Text = text, FromValue = from, ToValue = to });

    private static async Task<IResult> CreateAsync(
        string projectKey, CreateTicketRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (found, role) = await gate.OpenAsync(projectKey, ct);
        if (found is null || role is null)
        {
            return Results.NotFound();
        }
        if (!ProjectAccess.CanEdit(role))
        {
            return Results.Forbid();
        }
        var error = await ValidateAsync(db, found.Id, request.Title, request.AssigneeId, request.Estimate, request.LabelIds, ct, request.QaOwnerId);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        var actor = http.User.UserId();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var project = await db.Projects.FirstAsync(project => project.Id == found.Id, ct);
                project.LastTicketNumber++;
                var lowest = await db.Tickets.Where(ticket => ticket.ProjectId == project.Id && ticket.Status == TicketStatus.ToDo)
                    .MinAsync(ticket => (double?)ticket.Rank, ct);

                var ticket = new Ticket
                {
                    Project = project,
                    ProjectId = project.Id,
                    Number = project.LastTicketNumber,
                    Title = request.Title.Trim(),
                    Description = request.Description?.Trim(),
                    Type = request.Type,
                    Priority = request.Priority,
                    ReporterId = actor,
                    QaOwnerId = request.QaOwnerId,
                    EstimateMinutes = ParseEstimate(request.Estimate),
                    DueAt = request.DueAt,
                    Rank = Ranking.Between(null, lowest),
                    Labels = await LoadLabelsAsync(db, project.Id, request.LabelIds, ct)
                };
                TicketLifecycle.ApplyAssignee(ticket, request.AssigneeId, actor, DateTime.UtcNow);
                db.Tickets.Add(ticket);
                Record(db, ticket, actor, HistoryKind.Created, "created this ticket", to: ((int)TicketStatus.ToDo).ToString());
                if (ticket.AssigneeId is { } assigned)
                {
                    Record(db, ticket, actor, HistoryKind.Assignee, "assigned this ticket", null, assigned.ToString());
                }
                if (ticket.QaOwnerId is { } qa)
                {
                    Record(db, ticket, actor, HistoryKind.QaOwner, "set the QA owner", null, qa.ToString());
                }
                db.Publish(Snapshot(ticket));
                if (ticket.AssigneeId is { } assigneeId)
                {
                    db.Publish(new TicketAssigned(ticket.Id, ticket.Key, ticket.Title, assigneeId, actor));
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { key = ticket.Key });
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                // Someone else took this ticket number; start over with fresh state.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static async Task<IResult> UpdateAsync(
        string key, UpdateTicketRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (ticket, denied) = await OpenAsync(db, gate, key, ProjectAccess.CanEdit, ct);
        if (ticket is null)
        {
            return denied!;
        }
        var error = await ValidateAsync(db, ticket.ProjectId, request.Title, request.AssigneeId, request.Estimate, request.LabelIds, ct, request.QaOwnerId);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        var actor = http.User.UserId();
        var now = DateTime.UtcNow;

        var title = request.Title.Trim();
        if (title != ticket.Title)
        {
            Record(db, ticket, actor, HistoryKind.Title, "renamed this ticket", ticket.Title, title);
            ticket.Title = title;
        }
        if (request.Type != ticket.Type)
        {
            Record(db, ticket, actor, HistoryKind.Type, "changed the type", ticket.Type.ToString(), request.Type.ToString());
            ticket.Type = request.Type;
        }
        if (request.Priority != ticket.Priority)
        {
            Record(db, ticket, actor, HistoryKind.Priority, "changed the priority", ticket.Priority.ToString(), request.Priority.ToString());
            ticket.Priority = request.Priority;
        }
        if (request.DueAt != ticket.DueAt)
        {
            Record(db, ticket, actor, HistoryKind.DueDate, request.DueAt is null ? "removed the due date" : "changed the due date",
                ticket.DueAt?.ToString("O"), request.DueAt?.ToString("O"));
            ticket.DueAt = request.DueAt;
        }
        ticket.Description = request.Description?.Trim();

        ticket.Labels = await LoadLabelsAsync(db, ticket.ProjectId, request.LabelIds, ct);

        if (request.AssigneeId != ticket.AssigneeId)
        {
            Record(db, ticket, actor, HistoryKind.Assignee, request.AssigneeId is null ? "unassigned this ticket" : "changed the assignee",
                ticket.AssigneeId?.ToString(), request.AssigneeId?.ToString());
            TicketLifecycle.ApplyAssignee(ticket, request.AssigneeId, actor, now);
            if (request.AssigneeId is { } assigneeId)
            {
                db.Publish(new TicketAssigned(ticket.Id, ticket.Key, ticket.Title, assigneeId, actor));
            }
        }

        if (request.QaOwnerId != ticket.QaOwnerId)
        {
            Record(db, ticket, actor, HistoryKind.QaOwner, request.QaOwnerId is null ? "removed the QA owner" : "changed the QA owner",
                ticket.QaOwnerId?.ToString(), request.QaOwnerId?.ToString());
            var before = ticket.QaOwnerId;
            ticket.QaOwnerId = request.QaOwnerId;
            RequestReviewIfNeeded(db, ticket, ticket.Status, before, actor);
        }

        var estimate = ParseEstimate(request.Estimate);
        if (estimate != ticket.EstimateMinutes)
        {
            var old = ticket.EstimateMinutes;
            ticket.EstimateMinutes = estimate;
            Record(db, ticket, actor, HistoryKind.Estimate,
                $"changed the estimate to {(estimate is null ? "none" : JiraDuration.Format(estimate.Value))}", old?.ToString(), estimate?.ToString());
            db.Publish(new EstimateChanged(ticket.Id, ticket.Key, ticket.Title, old, estimate, actor,
                Recipients(ticket).Where(id => id != actor).ToList()));
            RaiseEstimateAlert(db, ticket);
        }

        return await SaveAsync(db, ticket, ct);
    }

    private static async Task<IResult> MoveAsync(
        string key, MoveTicketRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (ticket, denied) = await OpenAsync(db, gate, key, ProjectAccess.CanEdit, ct);
        if (ticket is null)
        {
            return denied!;
        }
        if (!Enum.IsDefined(request.Status))
        {
            return Results.BadRequest(new { error = "Unknown status." });
        }

        if (request.Status != ticket.Status)
        {
            var actor = http.User.UserId();
            var statusBefore = ticket.Status;
            Record(db, ticket, actor, HistoryKind.Status, $"moved this ticket to {Name(request.Status)}",
                ((int)statusBefore).ToString(), ((int)request.Status).ToString());
            TicketLifecycle.ApplyStatus(ticket, request.Status, actor, DateTime.UtcNow);
            RequestReviewIfNeeded(db, ticket, statusBefore, ticket.QaOwnerId, actor);
        }
        var after = await RankOfAsync(db, ticket.ProjectId, request.AfterKey, ct);
        var before = await RankOfAsync(db, ticket.ProjectId, request.BeforeKey, ct);
        if (after is null && before is null)
        {
            after = await db.Tickets
                .Where(other => other.ProjectId == ticket.ProjectId && other.Status == request.Status && other.Id != ticket.Id && other.DeletedAt == null)
                .MaxAsync(other => (double?)other.Rank, ct);
        }
        ticket.Rank = Ranking.Between(after, before);
        return await SaveAsync(db, ticket, ct);
    }

    private static async Task<IResult> CommentAsync(
        string key, AddCommentRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (ticket, denied) = await OpenAsync(db, gate, key, ProjectAccess.CanEdit, ct);
        if (ticket is null)
        {
            return denied!;
        }
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body) || body.Length > 10000)
        {
            return Results.BadRequest(new { error = "Enter a comment of 10,000 characters or fewer." });
        }

        var actor = http.User.UserId();
        db.Comments.Add(new Comment { TicketId = ticket.Id, AuthorId = actor, Body = body });
        db.Publish(new CommentAdded(ticket.Id, ticket.Key, ticket.Title, actor, http.User.DisplayName(),
            Recipients(ticket).Append(ticket.QaOwnerId ?? actor).Distinct().Where(id => id != actor).ToList()));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> LogWorkAsync(
        string key, LogWorkRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (ticket, denied) = await OpenAsync(db, gate, key, ProjectAccess.CanEdit, ct);
        if (ticket is null)
        {
            return denied!;
        }
        if (!JiraDuration.TryParse(request.Time, out var minutes))
        {
            return Results.BadRequest(new { error = "Enter time like 45m, 2h or 1d 4h." });
        }

        var actor = http.User.UserId();
        db.WorkLogs.Add(new WorkLog { TicketId = ticket.Id, UserId = actor, Minutes = minutes, Note = request.Note?.Trim() });
        Record(db, ticket, actor, HistoryKind.WorkLogged, $"logged {JiraDuration.Format(minutes)}", to: minutes.ToString());
        ticket.LoggedMinutes += minutes;
        RaiseEstimateAlert(db, ticket);
        return await SaveAsync(db, ticket, ct);
    }

    private static async Task<IResult> DeleteAsync(string key, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var (ticket, denied) = await OpenAsync(db, gate, key, ProjectAccess.CanAdminister, ct);
        if (ticket is null)
        {
            return denied!;
        }
        ticket.DeletedAt = DateTime.UtcNow;
        Record(db, ticket, http.User.UserId(), HistoryKind.Deleted, "deleted this ticket");
        return await SaveAsync(db, ticket, ct);
    }

    private static async Task<IResult> RestoreAsync(string key, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct)
    {
        var ticket = await FindAsync(db, key, ct, includeDeleted: true);
        if (ticket is null || !ProjectAccess.CanAdminister(await gate.RoleForAsync(ticket.ProjectId, ct)))
        {
            return Results.NotFound();
        }
        ticket.DeletedAt = null;
        Record(db, ticket, http.User.UserId(), HistoryKind.Restored, "restored this ticket");
        return await SaveAsync(db, ticket, ct);
    }

    private static void RequestReviewIfNeeded(WorkDb db, Ticket ticket, TicketStatus statusBefore, Guid? qaBefore, Guid actor)
    {
        if (TicketLifecycle.ShouldRequestReview(statusBefore, ticket.Status, qaBefore, ticket.QaOwnerId) && ticket.QaOwnerId != actor)
        {
            db.Publish(new TicketReadyForReview(ticket.Id, ticket.Key, ticket.Title, ticket.QaOwnerId!.Value, actor));
        }
    }

    private static void RaiseEstimateAlert(WorkDb db, Ticket ticket)
    {
        var (stored, alert) = EstimateRules.Evaluate(ticket.EstimateAlertLevel, ticket.EstimateMinutes, ticket.LoggedMinutes);
        ticket.EstimateAlertLevel = stored;
        if (alert is { } level)
        {
            db.Publish(new EstimateThresholdCrossed(ticket.Id, ticket.Key, ticket.Title, level,
                ticket.LoggedMinutes, ticket.EstimateMinutes!.Value, Recipients(ticket)));
        }
    }

    private static async Task<IResult> SaveAsync(WorkDb db, Ticket ticket, CancellationToken ct)
    {
        ticket.UpdatedAt = DateTime.UtcNow;
        ticket.Version++;
        db.Publish(Snapshot(ticket));
        try
        {
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "Someone else just changed this ticket. Reload and try again." });
        }
    }

    private static TicketChanged Snapshot(Ticket ticket) => new(
        ticket.Id, ticket.Version, ticket.Key, ticket.Title, ticket.AssigneeId, ticket.DueAt,
        ticket.Status == TicketStatus.Done, ticket.DeletedAt is not null);

    private static List<Guid> Recipients(Ticket ticket) =>
        new[] { ticket.AssigneeId, ticket.ReporterId }.OfType<Guid>().Distinct().ToList();

    private static string Name(TicketStatus status) => status switch
    {
        TicketStatus.ToDo => "To Do",
        TicketStatus.InProgress => "In Progress",
        TicketStatus.InReview => "In Review",
        _ => "Done"
    };

    private static int? ParseEstimate(string? estimate) =>
        JiraDuration.TryParse(estimate, out var minutes) ? minutes : null;

    private static async Task<string?> ValidateAsync(
        WorkDb db, Guid projectId, string? title, Guid? assigneeId, string? estimate, IReadOnlyList<Guid>? labelIds,
        CancellationToken ct, Guid? qaOwnerId = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 255)
        {
            return "Enter a title of 255 characters or fewer.";
        }
        if (!string.IsNullOrWhiteSpace(estimate) && !JiraDuration.TryParse(estimate, out _))
        {
            return "Enter estimates like 45m, 2h or 1d 4h.";
        }
        if (assigneeId is not null
            && !await db.ProjectMembers.AnyAsync(member => member.ProjectId == projectId && member.UserId == assigneeId, ct))
        {
            return "The assignee must be a member of this project.";
        }
        if (qaOwnerId is not null
            && !await db.ProjectMembers.AnyAsync(member => member.ProjectId == projectId && member.UserId == qaOwnerId, ct))
        {
            return "The QA owner must be a member of this project.";
        }
        if (labelIds is { Count: > 0 })
        {
            var known = await db.Labels.CountAsync(label => label.ProjectId == projectId && labelIds.Contains(label.Id), ct);
            if (known != labelIds.Distinct().Count())
            {
                return "One of those labels isn't in this project.";
            }
        }
        return null;
    }

    private static async Task<List<Label>> LoadLabelsAsync(WorkDb db, Guid projectId, IReadOnlyList<Guid>? labelIds, CancellationToken ct) =>
        labelIds is { Count: > 0 }
            ? await db.Labels.Where(label => label.ProjectId == projectId && labelIds.Contains(label.Id)).ToListAsync(ct)
            : [];

    private static async Task<double?> RankOfAsync(WorkDb db, Guid projectId, string? key, CancellationToken ct)
    {
        if (key is null || !TicketKey.TryParse(key, out _, out var number))
        {
            return null;
        }
        return await db.Tickets.Where(ticket => ticket.ProjectId == projectId && ticket.Number == number)
            .Select(ticket => (double?)ticket.Rank).FirstOrDefaultAsync(ct);
    }

    private static async Task<(Ticket? Ticket, IResult? Denied)> OpenAsync(
        WorkDb db, ProjectGate gate, string key, Func<ProjectRole?, bool> allowed, CancellationToken ct)
    {
        var ticket = await FindAsync(db, key, ct);
        if (ticket is null)
        {
            return (null, Results.NotFound());
        }
        var role = await gate.RoleForAsync(ticket.ProjectId, ct);
        if (role is null)
        {
            return (null, Results.NotFound());
        }
        return allowed(role) ? (ticket, null) : (null, Results.Forbid());
    }

    private static async Task<Ticket?> FindAsync(
        WorkDb db, string key, CancellationToken ct, bool details = false, bool includeDeleted = false)
    {
        if (!TicketKey.TryParse(key, out var projectKey, out var number))
        {
            return null;
        }
        var query = db.Tickets.Include(ticket => ticket.Project).Include(ticket => ticket.Labels).AsQueryable();
        if (details)
        {
            query = query.Include(ticket => ticket.Comments).Include(ticket => ticket.WorkLogs)
                .Include(ticket => ticket.Activity).AsSplitQuery();
        }
        return await query.FirstOrDefaultAsync(ticket => ticket.Project.Key == projectKey && ticket.Number == number
            && (includeDeleted || ticket.DeletedAt == null), ct);
    }

    private static async Task<Dictionary<Guid, UserCopy>> LoadUsersAsync(WorkDb db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(user => wanted.Contains(user.Id)).ToDictionaryAsync(user => user.Id, ct);
    }

    private static UserRef? Ref(Dictionary<Guid, UserCopy> users, Guid? id) =>
        id is { } value && users.TryGetValue(value, out var user) ? new UserRef(user.Id, user.DisplayName, user.AvatarUrl) : null;

    private static TicketCard ToCard(Ticket ticket, Dictionary<Guid, UserCopy> users) => new(
        ticket.Key, ticket.Title, ticket.Type, ticket.Status, ticket.Priority, Ref(users, ticket.AssigneeId),
        ticket.EstimateMinutes, ticket.LoggedMinutes,
        ticket.EstimateMinutes is { } estimate ? JiraDuration.Format(estimate) : null,
        JiraDuration.Format(ticket.LoggedMinutes), ticket.DueAt, ticket.Rank,
        ticket.Labels.OrderBy(label => label.Name).Select(label => new LabelView(label.Id, label.Name, label.Color)).ToList(),
        ticket.DeletedAt, Ref(users, ticket.QaOwnerId));
}
