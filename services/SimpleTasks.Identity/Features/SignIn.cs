using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Identity.Data;
using SimpleTasks.Identity.Security;
using SimpleTasks.Messaging;

namespace SimpleTasks.Identity.Features;

public sealed record SignInRequest(string Email, string DisplayName, string? AvatarUrl, string? GoogleSubject);
public sealed record SignInResponse(Guid UserId, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsSiteAdmin);
public sealed record TokenRequest(Guid UserId);

public static class SignInEndpoints
{
    public static void MapSignIn(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalKeyFilter>();

        group.MapPost("/sign-in", async (SignInRequest request, IdentityDb db, IConfiguration configuration, CancellationToken ct) =>
        {
            var user = await SignInAsync(db, request, configuration["Identity:FirstAdminEmail"], DateTime.UtcNow, ct);
            return user is null
                ? Results.Forbid()
                : Results.Ok(new SignInResponse(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.TimeZoneId, user.IsSiteAdmin));
        });

        group.MapPost("/tokens", async (TokenRequest request, IdentityDb db, TokenIssuer issuer, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == request.UserId, ct);
            if (user is null || !user.IsActive)
            {
                return Results.Forbid();
            }
            var (token, expiresAt) = issuer.Issue(user);
            return Results.Ok(new { token, expiresAt });
        });
    }

    /// <summary>Lets in the first admin, existing active users, and holders of a live invite. Nobody else.</summary>
    public static async Task<User?> SignInAsync(
        IdentityDb db, SignInRequest request, string? firstAdminEmail, DateTime now, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var isFirstAdmin = string.Equals(email, firstAdminEmail?.Trim(), StringComparison.OrdinalIgnoreCase);

        var user = await db.Users.FirstOrDefaultAsync(user => user.Email == email, ct);
        var invites = await db.Invites
            .Where(invite => invite.Email == email && invite.AcceptedAt == null && invite.ExpiresAt > now)
            .ToListAsync(ct);

        if (user is null)
        {
            if (!isFirstAdmin && invites.Count == 0)
            {
                return null;
            }
            user = new User { Email = email };
            db.Users.Add(user);
        }
        else if (!user.IsActive)
        {
            return null;
        }

        // A Google account stays bound to the user it first signed in as.
        if (request.GoogleSubject is not null)
        {
            if (user.GoogleSubject is not null && user.GoogleSubject != request.GoogleSubject)
            {
                return null;
            }
            user.GoogleSubject = request.GoogleSubject;
        }

        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email : request.DisplayName.Trim();
        user.AvatarUrl = request.AvatarUrl;
        user.IsSiteAdmin |= isFirstAdmin;

        db.Publish(user.ToChangedEvent());
        foreach (var invite in invites)
        {
            invite.AcceptedAt = now;
            if (invite.ProjectId is { } projectId && invite.Role is { } role)
            {
                db.Publish(new ProjectMembershipGranted(user.Id, projectId, role));
            }
        }

        await db.SaveChangesAsync(ct);
        return user;
    }

    public static UserChanged ToChangedEvent(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.TimeZoneId, user.IsActive);
}
