using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Identity.Data;
using SimpleTasks.Identity.Security;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;

namespace SimpleTasks.Identity.Features;

public sealed record CreateInviteRequest(string Email);
public sealed record InviteView(Guid Id, string Email, string? ProjectName, DateTime ExpiresAt, DateTime? AcceptedAt);

public static class Invites
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static void MapInvites(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invites").RequireAuthorization(policy => policy.RequireClaim(Jwt.SiteAdminClaim, "true"));

        group.MapGet("/", async (IdentityDb db, CancellationToken ct) =>
            await db.Invites.AsNoTracking()
                .OrderByDescending(invite => invite.CreatedAt)
                .Take(100)
                .Select(invite => new InviteView(invite.Id, invite.Email, invite.ProjectName, invite.ExpiresAt, invite.AcceptedAt))
                .ToListAsync(ct));

        group.MapPost("/", async (CreateInviteRequest request, HttpContext http, IdentityDb db, CancellationToken ct) =>
        {
            if (!IsEmail(request.Email))
            {
                return Results.BadRequest(new { error = "Enter a valid email address." });
            }
            var email = Login.Normalize(request.Email);
            if (await db.Users.AnyAsync(user => user.Email == email, ct))
            {
                return Results.Conflict(new { error = $"{email} already has an account." });
            }
            var invite = await CreateAsync(db, email, null, null, null, http.User.UserId(), DateTime.UtcNow, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new InviteView(invite.Id, invite.Email, null, invite.ExpiresAt, null));
        });
    }

    public static bool IsEmail(string email) =>
        email.Trim() is { Length: > 2 and <= 320 } trimmed && trimmed.IndexOf('@') is > 0 && !trimmed.EndsWith('@');

    /// <summary>Creates or renews an invite. Renewing issues a new link, so an older email stops working.</summary>
    public static async Task<Invite> CreateAsync(
        IdentityDb db, string email, Guid? projectId, string? projectName, ProjectRole? role,
        Guid invitedBy, DateTime now, CancellationToken ct)
    {
        var normalized = Login.Normalize(email);
        var existing = await db.Invites.FirstOrDefaultAsync(invite =>
            invite.Email == normalized && invite.ProjectId == projectId && invite.AcceptedAt == null, ct);

        var invite = existing ?? new Invite { Email = normalized, ProjectId = projectId, ProjectName = projectName };
        if (existing is null)
        {
            db.Invites.Add(invite);
        }

        var token = OneTimeToken.Create();
        invite.TokenHash = OneTimeToken.Hash(token);
        invite.Role = role;
        invite.InvitedBy = invitedBy;
        invite.ExpiresAt = now.Add(Lifetime);

        db.Publish(new InviteCreated(invite.Id, invite.Email, projectName, invite.ExpiresAt, token));
        return invite;
    }
}

/// <summary>Work decides who may invite to a project; Identity records the invite.</summary>
public sealed class ProjectInviteRequestedHandler(IdentityDb db) : IEventHandler<ProjectInviteRequested>
{
    public async Task HandleAsync(ProjectInviteRequested message, CancellationToken ct)
    {
        var email = Login.Normalize(message.Email);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Email == email, ct);
        if (user is not null)
        {
            db.Publish(new ProjectMembershipGranted(user.Id, message.ProjectId, message.Role));
            return;
        }
        await Invites.CreateAsync(db, email, message.ProjectId, message.ProjectName, message.Role, message.InvitedBy, DateTime.UtcNow, ct);
    }
}

/// <summary>Invites to a deleted project can no longer be accepted, so they are removed.</summary>
public sealed class ProjectDeletedHandler(IdentityDb db) : IEventHandler<ProjectDeleted>
{
    public async Task HandleAsync(ProjectDeleted message, CancellationToken ct)
    {
        var pending = await db.Invites.Where(invite => invite.ProjectId == message.ProjectId && invite.AcceptedAt == null).ToListAsync(ct);
        db.Invites.RemoveRange(pending);
    }
}
