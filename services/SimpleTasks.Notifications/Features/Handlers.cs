using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.Notifications.Data;

namespace SimpleTasks.Notifications.Features;

public sealed class UserChangedHandler(NotificationsDb db) : IEventHandler<UserChanged>
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
        user.TimeZoneId = message.TimeZoneId;
        user.IsActive = message.IsActive;
    }
}

public sealed class TicketChangedHandler(NotificationsDb db) : IEventHandler<TicketChanged>
{
    public async Task HandleAsync(TicketChanged message, CancellationToken ct)
    {
        var ticket = await db.Tickets.FindAsync([message.TicketId], ct);
        if (ticket is null)
        {
            ticket = new TicketCopy { Id = message.TicketId };
            db.Tickets.Add(ticket);
        }
        else if (message.Version < ticket.Version)
        {
            return;
        }

        if (ticket.DueAt != message.DueAt)
        {
            ticket.LastOverdueSentAt = null;
        }
        ticket.Version = message.Version;
        ticket.Key = message.Key;
        ticket.Title = message.Title;
        ticket.AssigneeId = message.AssigneeId;
        ticket.DueAt = message.DueAt;
        ticket.IsDone = message.IsDone;
        ticket.IsDeleted = message.IsDeleted;
    }
}

public sealed class InviteCreatedHandler(NotificationsDb db) : IEventHandler<InviteCreated>
{
    public Task HandleAsync(InviteCreated message, CancellationToken ct)
    {
        var target = message.ProjectName is null ? "Simple Tasks" : $"the {message.ProjectName} project on Simple Tasks";
        db.Emails.Add(new EmailMessage
        {
            To = message.Email,
            Subject = $"You're invited to {target}",
            Body = $"You've been invited to {target}. Open the link to choose your password. It works once and expires on {message.ExpiresAt:dd MMM yyyy} (UTC).",
            LinkPath = message.SetupToken is null ? "/" : $"/Account/SetPassword?token={Uri.EscapeDataString(message.SetupToken)}"
        });
        return Task.CompletedTask;
    }
}

public sealed class PasswordResetRequestedHandler(NotificationsDb db) : IEventHandler<PasswordResetRequested>
{
    public Task HandleAsync(PasswordResetRequested message, CancellationToken ct)
    {
        db.Emails.Add(new EmailMessage
        {
            To = message.Email,
            Subject = "Set your Simple Tasks password",
            Body = $"Open the link to set a new password. It works once and expires at {message.ExpiresAt:dd MMM yyyy HH:mm} (UTC). If you didn't ask for this, you can ignore this email.",
            LinkPath = $"/Account/SetPassword?token={Uri.EscapeDataString(message.Token)}"
        });
        return Task.CompletedTask;
    }
}

public sealed class TicketAssignedHandler(NotificationWriter writer) : IEventHandler<TicketAssigned>
{
    public async Task HandleAsync(TicketAssigned message, CancellationToken ct)
    {
        if (message.AssigneeId != message.ActorId)
        {
            await writer.NotifyAsync(message.AssigneeId, NotificationType.Assigned,
                $"{message.Key} was assigned to you", message.Title, message.Key, ct);
        }
    }
}

public sealed class CommentAddedHandler(NotificationWriter writer) : IEventHandler<CommentAdded>
{
    public async Task HandleAsync(CommentAdded message, CancellationToken ct)
    {
        foreach (var userId in message.Recipients)
        {
            await writer.NotifyAsync(userId, NotificationType.Comment,
                $"{message.ActorName} commented on {message.Key}", message.Title, message.Key, ct);
        }
    }
}

public sealed class EstimateChangedHandler(NotificationWriter writer) : IEventHandler<EstimateChanged>
{
    public async Task HandleAsync(EstimateChanged message, CancellationToken ct)
    {
        var body = $"{message.Title}: estimate changed from {Hours(message.OldMinutes)} to {Hours(message.NewMinutes)}.";
        foreach (var userId in message.Recipients)
        {
            await writer.NotifyAsync(userId, NotificationType.EstimateChanged,
                $"The estimate for {message.Key} changed", body, message.Key, ct);
        }
    }

    private static string Hours(int? minutes) => minutes is { } value ? $"{value / 60.0:0.##}h" : "none";
}

public sealed class EstimateThresholdCrossedHandler(NotificationWriter writer) : IEventHandler<EstimateThresholdCrossed>
{
    public async Task HandleAsync(EstimateThresholdCrossed message, CancellationToken ct)
    {
        var percent = message.LoggedMinutes * 100 / message.EstimateMinutes;
        var title = message.Level == EstimateAlertLevel.Exceeded
            ? $"{message.Key} has exceeded its estimate"
            : $"{message.Key} has used {percent}% of its estimate";
        var body = $"{message.Title}: {message.LoggedMinutes / 60.0:0.##}h logged of {message.EstimateMinutes / 60.0:0.##}h estimated.";
        foreach (var userId in message.Recipients)
        {
            await writer.NotifyAsync(userId, NotificationType.EstimateAlert, title, body, message.Key, ct);
        }
    }
}
