using Microsoft.EntityFrameworkCore;
using SimpleTasks.Identity.Data;
using SimpleTasks.Messaging;
using SimpleTasks.ServiceDefaults;

namespace SimpleTasks.Identity.Features;

public sealed record UserView(Guid Id, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsSiteAdmin, bool IsActive);
public sealed record UpdateProfileRequest(string TimeZoneId);
public sealed record SetActiveRequest(bool IsActive);

public static class Users
{
    public static void MapUsers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (HttpContext http, IdentityDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await db.Users.AsNoTracking().Where(user => user.Id == userId).Select(ToView).FirstOrDefaultAsync(ct);
        }).RequireAuthorization();

        app.MapPut("/api/me", async (UpdateProfileRequest request, HttpContext http, IdentityDb db, CancellationToken ct) =>
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out _))
            {
                return Results.BadRequest(new { error = "Unknown time zone." });
            }
            var user = await db.Users.FindAsync([http.User.UserId()], ct);
            if (user is null)
            {
                return Results.NotFound();
            }
            user.TimeZoneId = request.TimeZoneId;
            db.Publish(user.ToChangedEvent());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/users").RequireAuthorization(policy => policy.RequireClaim(Jwt.SiteAdminClaim, "true"));

        admin.MapGet("/", async (IdentityDb db, CancellationToken ct) =>
            await db.Users.AsNoTracking().OrderBy(user => user.DisplayName).Select(ToView).ToListAsync(ct));

        admin.MapPost("/{id:guid}/active", async (Guid id, SetActiveRequest request, HttpContext http, IdentityDb db, CancellationToken ct) =>
        {
            if (id == http.User.UserId())
            {
                return Results.BadRequest(new { error = "You can't deactivate your own account." });
            }
            var user = await db.Users.FindAsync([id], ct);
            if (user is null)
            {
                return Results.NotFound();
            }
            user.IsActive = request.IsActive;
            db.Publish(user.ToChangedEvent());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static readonly System.Linq.Expressions.Expression<Func<User, UserView>> ToView = user =>
        new UserView(user.Id, user.Email, user.DisplayName, user.AvatarUrl, user.TimeZoneId, user.IsSiteAdmin, user.IsActive);
}

/// <summary>Accounts for the dev sign-in. Only ever called in the Development environment.</summary>
public static class DevUsers
{
    public static readonly string[] Emails = ["dev.member@simpletasks.test", "dev.viewer@simpletasks.test"];

    public static async Task SeedAsync(IdentityDb db)
    {
        foreach (var email in Emails)
        {
            if (!await db.Users.AnyAsync(user => user.Email == email))
            {
                var user = new User { Email = email, DisplayName = email.Split('@')[0] };
                db.Users.Add(user);
                db.Publish(user.ToChangedEvent());
            }
        }
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// Makes sure the configured first admin exists. Until they have a password, every start
/// emails and logs a link to set one, so they can get in before email is set up.
/// </summary>
public static class FirstAdmin
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    public static async Task EnsureAsync(IdentityDb db, string? configuredEmail, string baseUrl, ILogger logger)
    {
        var email = Login.Normalize(configuredEmail);
        if (email.Length == 0)
        {
            return;
        }

        var admin = await db.Users.FirstOrDefaultAsync(user => user.Email == email);
        if (admin is null)
        {
            admin = new User { Email = email, DisplayName = email.Split('@')[0], IsSiteAdmin = true };
            db.Users.Add(admin);
            db.Publish(admin.ToChangedEvent());
        }
        admin.IsSiteAdmin = true;

        if (admin.PasswordHash is null)
        {
            var token = Login.IssueReset(db, admin, LinkLifetime, DateTime.UtcNow);
            logger.LogWarning(
                "The first admin {Email} has no password yet. Set one within 24 hours at {Url}",
                email, $"{baseUrl.TrimEnd('/')}/Account/SetPassword?token={token}");
        }
        await db.SaveChangesAsync();
    }
}
