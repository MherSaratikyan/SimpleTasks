using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Identity.Data;
using SimpleTasks.Identity.Security;
using SimpleTasks.Messaging;

namespace SimpleTasks.Identity.Features;

public sealed record LoginRequest(string Email, string Password);
public sealed record UserResponse(Guid UserId, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsSiteAdmin);
public sealed record ResetRequest(string Email);
public sealed record SetPasswordRequest(string Token, string Password, string? DisplayName);
public sealed record TokenInfo(string Kind, string Email);
public sealed record TokenRequest(Guid UserId);

public static class Login
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    // Checked against when an email is unknown, so a reply takes as long either way.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(new User(), "not-a-real-password");

    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter<InternalKeyFilter>();

        group.MapPost("/login", async (LoginRequest request, IdentityDb db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var email = Normalize(request.Email);
            var user = await db.Users.FirstOrDefaultAsync(user => user.Email == email, ct);

            if (user is null || !user.IsActive || user.PasswordHash is null)
            {
                hasher.VerifyHashedPassword(new User(), DummyHash, request.Password ?? string.Empty);
                return Results.Json(new { error = "Wrong email or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (Lockout.IsLocked(user, now))
            {
                return Results.Json(new { error = "Too many failed attempts. Try again in 15 minutes, or reset your password." },
                    statusCode: StatusCodes.Status423Locked);
            }

            var result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? string.Empty);
            if (result == PasswordVerificationResult.Failed)
            {
                Lockout.RecordFailure(user, now);
                await db.SaveChangesAsync(ct);
                return Results.Json(new { error = "Wrong email or password." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, request.Password!);
            }

            Lockout.Clear(user);
            await db.SaveChangesAsync(ct);
            return Results.Ok(user.ToResponse());
        });

        // Always the same answer, so this can't be used to find out who has an account.
        group.MapPost("/password-reset", async (ResetRequest request, IdentityDb db, CancellationToken ct) =>
        {
            var email = Normalize(request.Email);
            var user = await db.Users.FirstOrDefaultAsync(user => user.Email == email && user.IsActive, ct);
            if (user is not null)
            {
                IssueReset(db, user, ResetLifetime, DateTime.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            return Results.Accepted();
        });

        group.MapGet("/password-token", async (string token, IdentityDb db, CancellationToken ct) =>
        {
            var found = await FindTokenAsync(db, token, DateTime.UtcNow, ct);
            return found switch
            {
                (Invite invite, _) => Results.Ok(new TokenInfo("invite", invite.Email)),
                (_, (PasswordReset, User user)) => Results.Ok(new TokenInfo("reset", user.Email)),
                _ => Results.NotFound()
            };
        });

        group.MapPost("/password", async (SetPasswordRequest request, IdentityDb db, IPasswordHasher<User> hasher, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var found = await FindTokenAsync(db, request.Token, now, ct);
            if (found is (null, null))
            {
                return Results.BadRequest(new { error = "This link has expired or was already used. Ask for a new one." });
            }

            User user;
            if (found.Invite is { } invite)
            {
                user = await db.Users.FirstOrDefaultAsync(user => user.Email == invite.Email, ct) ?? new User { Email = invite.Email };
                if (db.Entry(user).State == EntityState.Detached)
                {
                    var name = request.DisplayName?.Trim();
                    if (string.IsNullOrEmpty(name) || name.Length > 100)
                    {
                        return Results.BadRequest(new { error = "Enter your name, 100 characters or fewer." });
                    }
                    user.DisplayName = name;
                    db.Users.Add(user);
                }
                else if (!user.IsActive)
                {
                    return Results.BadRequest(new { error = "This account has been deactivated." });
                }
            }
            else
            {
                (var reset, user) = found.Reset!.Value;
                reset.UsedAt = now;
            }

            if (PasswordRules.Problem(request.Password, user.Email) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            user.PasswordHash = hasher.HashPassword(user, request.Password);
            Lockout.Clear(user);
            await db.PasswordResets
                .Where(reset => reset.UserId == user.Id && reset.UsedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(reset => reset.UsedAt, now), ct);
            await AcceptInvitesAsync(db, user, now, ct);
            db.Publish(user.ToChangedEvent());
            await db.SaveChangesAsync(ct);
            return Results.Ok(user.ToResponse());
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

    /// <summary>Creates a reset link and asks Notifications to email it. Returns the token for the first-admin log line.</summary>
    public static string IssueReset(IdentityDb db, User user, TimeSpan lifetime, DateTime now)
    {
        var token = OneTimeToken.Create();
        var reset = new PasswordReset { UserId = user.Id, TokenHash = OneTimeToken.Hash(token), ExpiresAt = now + lifetime };
        db.PasswordResets.Add(reset);
        db.Publish(new PasswordResetRequested(user.Email, token, reset.ExpiresAt));
        return token;
    }

    private static async Task<(Invite? Invite, (PasswordReset Reset, User User)? Reset)> FindTokenAsync(
        IdentityDb db, string? token, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return (null, null);
        }
        var hash = OneTimeToken.Hash(token);

        var invite = await db.Invites.FirstOrDefaultAsync(invite =>
            invite.TokenHash == hash && invite.AcceptedAt == null && invite.ExpiresAt > now, ct);
        if (invite is not null)
        {
            return (invite, null);
        }

        var reset = await db.PasswordResets.FirstOrDefaultAsync(reset =>
            reset.TokenHash == hash && reset.UsedAt == null && reset.ExpiresAt > now, ct);
        var user = reset is null ? null : await db.Users.FirstOrDefaultAsync(user => user.Id == reset.UserId && user.IsActive, ct);
        return reset is not null && user is not null ? (null, (reset, user)) : (null, null);
    }

    /// <summary>Accepting one invite accepts them all, granting every project membership waiting for this email.</summary>
    private static async Task AcceptInvitesAsync(IdentityDb db, User user, DateTime now, CancellationToken ct)
    {
        var invites = await db.Invites
            .Where(invite => invite.Email == user.Email && invite.AcceptedAt == null && invite.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var invite in invites)
        {
            invite.AcceptedAt = now;
            if (invite.ProjectId is { } projectId && invite.Role is { } role)
            {
                db.Publish(new ProjectMembershipGranted(user.Id, projectId, role));
            }
        }
    }

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static UserResponse ToResponse(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.TimeZoneId, user.IsSiteAdmin);

    public static UserChanged ToChangedEvent(this User user) =>
        new(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.TimeZoneId, user.IsActive);
}
