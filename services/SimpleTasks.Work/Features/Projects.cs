using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Domain;

namespace SimpleTasks.Work.Features;

public sealed record ProjectView(Guid Id, string Key, string Name, string? Description, ProjectRole Role);
public sealed record CreateProjectRequest(string Key, string Name, string? Description);
public sealed record MemberView(Guid UserId, string DisplayName, string Email, string? AvatarUrl, ProjectRole Role);
public sealed record InviteMemberRequest(string Email, ProjectRole Role);
public sealed record SetRoleRequest(ProjectRole Role);
public sealed record LabelView(Guid Id, string Name, string Color);
public sealed record SaveLabelRequest(string Name, string Color);

/// <summary>Resolves the caller's effective role. A project they can't see behaves as if it doesn't exist.</summary>
public sealed class ProjectGate(WorkDb db, IHttpContextAccessor http)
{
    public async Task<(Project? Project, ProjectRole? Role)> OpenAsync(string key, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(project => project.Key == key.ToUpper(), ct);
        return project is null ? (null, null) : (project, await RoleForAsync(project.Id, ct));
    }

    public async Task<ProjectRole?> RoleForAsync(Guid projectId, CancellationToken ct)
    {
        var user = http.HttpContext!.User;
        var userId = user.UserId();
        var membership = await db.ProjectMembers.AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.UserId == userId)
            .Select(member => (ProjectRole?)member.Role)
            .FirstOrDefaultAsync(ct);
        return ProjectAccess.EffectiveRole(membership, user.IsSiteAdmin());
    }
}

public static class Projects
{
    public static void MapProjects(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", async (HttpContext http, WorkDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var isSiteAdmin = http.User.IsSiteAdmin();
            return await (
                from project in db.Projects.AsNoTracking()
                join member in db.ProjectMembers.Where(member => member.UserId == userId)
                    on project.Id equals member.ProjectId into memberships
                from member in memberships.DefaultIfEmpty()
                where isSiteAdmin || member != null
                orderby project.Name
                select new ProjectView(project.Id, project.Key, project.Name, project.Description,
                    isSiteAdmin ? ProjectRole.Admin : member!.Role))
                .ToListAsync(ct);
        });

        group.MapPost("/", async (CreateProjectRequest request, HttpContext http, WorkDb db, CancellationToken ct) =>
        {
            var key = request.Key.Trim().ToUpperInvariant();
            if (!TicketKey.IsValidProjectKey(key))
            {
                return Results.BadRequest(new { error = "Keys are 2-10 letters or digits and start with a letter." });
            }
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
            {
                return Results.BadRequest(new { error = "Enter a project name of 100 characters or fewer." });
            }
            if (await db.Projects.AnyAsync(project => project.Key == key, ct))
            {
                return Results.Conflict(new { error = $"The key {key} is already in use." });
            }

            var project = new Project { Key = key, Name = request.Name.Trim(), Description = request.Description?.Trim() };
            db.Projects.Add(project);
            db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = http.User.UserId(), Role = ProjectRole.Admin });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ProjectView(project.Id, project.Key, project.Name, project.Description, ProjectRole.Admin));
        }).RequireAuthorization(policy => policy.RequireClaim(Jwt.SiteAdminClaim, "true"));

        group.MapGet("/{key}", async (string key, ProjectGate gate, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            return project is null || role is null
                ? Results.NotFound()
                : Results.Ok(new ProjectView(project.Id, project.Key, project.Name, project.Description, role.Value));
        });

        // Permanent. The caller must type the project key, and other services are told to clean up.
        group.MapDelete("/{key}", async (string key, string? confirm, WorkDb db, CancellationToken ct) =>
        {
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(project => project.Key == key.ToUpper(), ct);
            if (project is null)
            {
                return Results.NotFound();
            }
            if (!string.Equals(confirm?.Trim(), project.Key, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Type {project.Key} to confirm." });
            }

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var ticketIds = await db.Tickets.Where(ticket => ticket.ProjectId == project.Id).Select(ticket => ticket.Id).ToListAsync(ct);

                // Comments, work logs, history and label links go with their tickets; labels and members with the project.
                await db.Tickets.Where(ticket => ticket.ProjectId == project.Id).ExecuteDeleteAsync(ct);
                await db.Projects.Where(item => item.Id == project.Id).ExecuteDeleteAsync(ct);

                db.Publish(new ProjectDeleted(project.Id, project.Key, ticketIds));
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                db.ChangeTracker.Clear();
            });
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireClaim(Jwt.SiteAdminClaim, "true"));

        MapMembers(group);
        MapLabels(group);
    }

    private static void MapMembers(RouteGroupBuilder group)
    {
        group.MapGet("/{key}/members", async (string key, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            var members = await (
                from member in db.ProjectMembers.AsNoTracking()
                join user in db.Users.AsNoTracking() on member.UserId equals user.Id into users
                from user in users.DefaultIfEmpty()
                where member.ProjectId == project.Id
                select new MemberView(member.UserId, user != null ? user.DisplayName : "Unknown user",
                    user != null ? user.Email : string.Empty, user != null ? user.AvatarUrl : null, member.Role))
                .ToListAsync(ct);
            return Results.Ok(members.OrderBy(member => member.DisplayName));
        });

        group.MapPost("/{key}/invites", async (string key, InviteMemberRequest request, HttpContext http, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            if (!ProjectAccess.CanAdminister(role))
            {
                return Results.Forbid();
            }
            if (!request.Email.Contains('@') || !Enum.IsDefined(request.Role))
            {
                return Results.BadRequest(new { error = "Enter a valid email address and role." });
            }
            db.Publish(new ProjectInviteRequested(request.Email.Trim(), project.Id, project.Name, request.Role, http.User.UserId()));
            await db.SaveChangesAsync(ct);
            return Results.Accepted();
        });

        group.MapPut("/{key}/members/{userId:guid}", async (string key, Guid userId, SetRoleRequest request, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            if (!ProjectAccess.CanAdminister(role))
            {
                return Results.Forbid();
            }
            var member = await db.ProjectMembers.FindAsync([project.Id, userId], ct);
            if (member is null || !Enum.IsDefined(request.Role))
            {
                return Results.NotFound();
            }
            if (request.Role != ProjectRole.Admin && await IsLastAdminAsync(db, member, ct))
            {
                return Results.BadRequest(new { error = "A project needs at least one Project Admin." });
            }
            member.Role = request.Role;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{key}/members/{userId:guid}", async (string key, Guid userId, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            if (!ProjectAccess.CanAdminister(role))
            {
                return Results.Forbid();
            }
            var member = await db.ProjectMembers.FindAsync([project.Id, userId], ct);
            if (member is null)
            {
                return Results.NotFound();
            }
            if (await IsLastAdminAsync(db, member, ct))
            {
                return Results.BadRequest(new { error = "A project needs at least one Project Admin." });
            }
            db.ProjectMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<bool> IsLastAdminAsync(WorkDb db, ProjectMember member, CancellationToken ct) =>
        member.Role == ProjectRole.Admin
        && await db.ProjectMembers.CountAsync(other => other.ProjectId == member.ProjectId && other.Role == ProjectRole.Admin, ct) == 1;

    private static void MapLabels(RouteGroupBuilder group)
    {
        group.MapGet("/{key}/labels", async (string key, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(await db.Labels.AsNoTracking()
                .Where(label => label.ProjectId == project.Id)
                .OrderBy(label => label.Name)
                .Select(label => new LabelView(label.Id, label.Name, label.Color))
                .ToListAsync(ct));
        });

        group.MapPost("/{key}/labels", async (string key, SaveLabelRequest request, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            if (!ProjectAccess.CanAdminister(role))
            {
                return Results.Forbid();
            }
            var name = request.Name.Trim();
            if (name.Length is 0 or > 50 || !IsColor(request.Color))
            {
                return Results.BadRequest(new { error = "Enter a label name of 50 characters or fewer and a colour." });
            }
            if (await db.Labels.AnyAsync(label => label.ProjectId == project.Id && label.Name == name, ct))
            {
                return Results.Conflict(new { error = $"The label {name} already exists." });
            }
            var label = new Label { ProjectId = project.Id, Name = name, Color = request.Color };
            db.Labels.Add(label);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new LabelView(label.Id, label.Name, label.Color));
        });

        group.MapDelete("/{key}/labels/{id:guid}", async (string key, Guid id, ProjectGate gate, WorkDb db, CancellationToken ct) =>
        {
            var (project, role) = await gate.OpenAsync(key, ct);
            if (project is null || role is null)
            {
                return Results.NotFound();
            }
            if (!ProjectAccess.CanAdminister(role))
            {
                return Results.Forbid();
            }
            await db.Labels.Where(label => label.Id == id && label.ProjectId == project.Id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }

    private static bool IsColor(string color) =>
        color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
}

public sealed class UserChangedHandler(WorkDb db) : IEventHandler<UserChanged>
{
    public async Task HandleAsync(UserChanged message, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([message.UserId], ct);
        if (user is null)
        {
            user = new UserCopy { Id = message.UserId };
            db.Users.Add(user);
        }
        user.Email = message.Email;
        user.DisplayName = message.DisplayName;
        user.AvatarUrl = message.AvatarUrl;
        user.IsActive = message.IsActive;
    }
}

public sealed class ProjectMembershipGrantedHandler(WorkDb db) : IEventHandler<ProjectMembershipGranted>
{
    public async Task HandleAsync(ProjectMembershipGranted message, CancellationToken ct)
    {
        var member = await db.ProjectMembers.FindAsync([message.ProjectId, message.UserId], ct);
        if (member is null)
        {
            db.ProjectMembers.Add(new ProjectMember { ProjectId = message.ProjectId, UserId = message.UserId, Role = message.Role });
        }
        else
        {
            member.Role = message.Role;
        }
    }
}
