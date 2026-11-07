using SimpleTasks.Contracts;

namespace SimpleTasks.Services;

public static class Ui
{
    public static readonly TicketStatus[] Statuses = Enum.GetValues<TicketStatus>();

    public static string Name(this TicketStatus status) => status switch
    {
        TicketStatus.ToDo => "To Do",
        TicketStatus.InProgress => "In Progress",
        TicketStatus.InReview => "In Review",
        _ => "Done"
    };

    public static string Css(this TicketStatus status) => status switch
    {
        TicketStatus.InProgress or TicketStatus.InReview => "lozenge-progress",
        TicketStatus.Done => "lozenge-done",
        _ => "lozenge-todo"
    };

    public static string Name(this ProjectRole role) => role switch
    {
        ProjectRole.Admin => "Project Admin",
        ProjectRole.Member => "Member",
        _ => "Viewer"
    };

    public static string Name(this NotificationType type) => type switch
    {
        NotificationType.Assigned => "A ticket is assigned to me",
        NotificationType.Comment => "Someone comments on my ticket",
        NotificationType.EstimateChanged => "An estimate changes",
        NotificationType.EstimateAlert => "A ticket reaches 80% or exceeds its estimate",
        NotificationType.DueReminder => "Due date reminders",
        _ => "My own reminders"
    };

    public static string Initials(string? name)
    {
        var parts = (name ?? "?").Split([' ', '.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    public static int PercentUsed(TicketCard card) =>
        card.EstimateMinutes is > 0 ? Math.Min(100, card.LoggedMinutes * 100 / card.EstimateMinutes.Value) : 0;
}
