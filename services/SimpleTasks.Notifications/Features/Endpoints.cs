using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Notifications.Data;
using SimpleTasks.ServiceDefaults;

namespace SimpleTasks.Notifications.Features;

public sealed record NotificationView(Guid Id, NotificationType Type, string Title, string Body, string Url, DateTime CreatedAt, bool IsRead);
public sealed record NotificationFeed(int Unread, IReadOnlyList<NotificationView> Items);
public sealed record PreferenceView(NotificationType Type, bool Email);
public sealed record ReminderView(Guid Id, string TicketKey, string? Note, DateTime RemindAt);
public sealed record CreateReminderRequest(string TicketKey, DateTime RemindAt, string? Note);

public static class Endpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/notifications", async (HttpContext http, NotificationsDb db, CancellationToken ct, int take = 20) =>
        {
            var userId = http.User.UserId();
            var mine = db.Notifications.AsNoTracking().Where(notification => notification.UserId == userId);
            var unread = await mine.CountAsync(notification => notification.ReadAt == null, ct);
            var items = await mine.OrderByDescending(notification => notification.CreatedAt)
                .Take(Math.Clamp(take, 1, 100))
                .Select(notification => new NotificationView(notification.Id, notification.Type, notification.Title,
                    notification.Body, notification.Url, notification.CreatedAt, notification.ReadAt != null))
                .ToListAsync(ct);
            return new NotificationFeed(unread, items);
        });

        api.MapPost("/notifications/{id:guid}/read", async (Guid id, HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.Notifications.Where(notification => notification.Id == id && notification.UserId == userId && notification.ReadAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(notification => notification.ReadAt, DateTime.UtcNow), ct);
            return Results.NoContent();
        });

        api.MapPost("/notifications/read-all", async (HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.Notifications.Where(notification => notification.UserId == userId && notification.ReadAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(notification => notification.ReadAt, DateTime.UtcNow), ct);
            return Results.NoContent();
        });

        api.MapGet("/preferences", async (HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var optedOut = await db.EmailOptOuts.Where(optOut => optOut.UserId == userId).Select(optOut => optOut.Type).ToListAsync(ct);
            return Enum.GetValues<NotificationType>().Select(type => new PreferenceView(type, !optedOut.Contains(type)));
        });

        api.MapPut("/preferences", async (IReadOnlyList<PreferenceView> preferences, HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.EmailOptOuts.Where(optOut => optOut.UserId == userId).ExecuteDeleteAsync(ct);
            db.EmailOptOuts.AddRange(preferences.Where(preference => !preference.Email)
                .Select(preference => preference.Type).Distinct()
                .Select(type => new EmailOptOut { UserId = userId, Type = type }));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // The web app confirms the caller can see the ticket in Work before calling these.
        api.MapGet("/reminders", async (string ticketKey, HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            return await (
                from reminder in db.Reminders.AsNoTracking()
                join ticket in db.Tickets.AsNoTracking() on reminder.TicketId equals ticket.Id
                where reminder.UserId == userId && reminder.FiredAt == null && ticket.Key == ticketKey
                orderby reminder.RemindAt
                select new ReminderView(reminder.Id, ticket.Key, reminder.Note, reminder.RemindAt))
                .ToListAsync(ct);
        });

        api.MapPost("/reminders", async (CreateReminderRequest request, HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            if (request.RemindAt <= DateTime.UtcNow)
            {
                return Results.BadRequest(new { error = "Pick a time in the future." });
            }
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket => ticket.Key == request.TicketKey, ct);
            if (ticket is null)
            {
                return Results.BadRequest(new { error = "This ticket isn't ready for reminders yet. Try again in a moment." });
            }
            db.Reminders.Add(new Reminder
            {
                UserId = http.User.UserId(),
                TicketId = ticket.Id,
                RemindAt = request.RemindAt,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapDelete("/reminders/{id:guid}", async (Guid id, HttpContext http, NotificationsDb db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            await db.Reminders.Where(reminder => reminder.Id == id && reminder.UserId == userId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        app.MapHub<NotificationHub>("/hubs/notifications");
    }
}
