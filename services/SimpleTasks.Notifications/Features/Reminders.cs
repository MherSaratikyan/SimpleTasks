using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;
using SimpleTasks.Notifications.Data;
using SimpleTasks.Notifications.Domain;

namespace SimpleTasks.Notifications.Features;

public sealed class ReminderRunner(NotificationsDb db, NotificationWriter writer)
{
    public async Task RunAsync(DateTime now, CancellationToken ct)
    {
        await SendDueDateRemindersAsync(now, ct);
        await SendCustomRemindersAsync(now, ct);
        await db.SaveChangesAsync(ct);
        await writer.RunAsync(ct);
    }

    private async Task SendDueDateRemindersAsync(DateTime now, CancellationToken ct)
    {
        var horizon = now + ReminderRules.DueSoonWindow;
        var tickets = await db.Tickets
            .Where(ticket => ticket.DueAt != null && ticket.DueAt <= horizon && ticket.AssigneeId != null && !ticket.IsDone && !ticket.IsDeleted)
            .ToListAsync(ct);

        foreach (var ticket in tickets)
        {
            var kind = ReminderRules.Next(
                ticket.DueAt, ticket.IsDone || ticket.IsDeleted, ticket.AssigneeId is not null,
                ticket.DueSoonSentFor, ticket.LastOverdueSentAt, now);
            if (kind == DueReminderKind.None)
            {
                continue;
            }

            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == ticket.AssigneeId, ct);
            var when = ReminderRules.FormatFor(ticket.DueAt!.Value, user?.TimeZoneId);
            if (kind == DueReminderKind.DueSoon)
            {
                ticket.DueSoonSentFor = ticket.DueAt;
                await writer.NotifyAsync(ticket.AssigneeId!.Value, NotificationType.DueReminder,
                    $"{ticket.Key} is due soon", $"{ticket.Title} is due {when}.", ticket.Key, ct);
            }
            else
            {
                ticket.LastOverdueSentAt = now;
                await writer.NotifyAsync(ticket.AssigneeId!.Value, NotificationType.DueReminder,
                    $"{ticket.Key} is overdue", $"{ticket.Title} was due {when}.", ticket.Key, ct);
            }
        }
    }

    private async Task SendCustomRemindersAsync(DateTime now, CancellationToken ct)
    {
        var due = await db.Reminders.Where(reminder => reminder.FiredAt == null && reminder.RemindAt <= now).ToListAsync(ct);
        foreach (var reminder in due)
        {
            reminder.FiredAt = now;
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(ticket => ticket.Id == reminder.TicketId, ct);
            if (ticket is null || ticket.IsDone || ticket.IsDeleted)
            {
                continue;
            }
            await writer.NotifyAsync(reminder.UserId, NotificationType.CustomReminder,
                $"Reminder: {ticket.Key}", reminder.Note ?? ticket.Title, ticket.Key, ct);
        }
    }
}

public sealed class ReminderWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Reminders:IntervalSeconds", 60));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ReminderRunner>().RunAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Reminder pass failed; trying again next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
