using Microsoft.AspNetCore.Html;
using SimpleTasks.Contracts;

namespace SimpleTasks.Services;

public static class Ui
{
    public static readonly TicketStatus[] Statuses = Enum.GetValues<TicketStatus>();

    private const string IconSprite = "/icons.svg?v=1";

    /// <summary>An icon from the bundled Lucide sprite. Names are fixed strings in our own markup, never user input.</summary>
    public static IHtmlContent Icon(string name, string? css = null) =>
        new HtmlString($"<svg class=\"icon {css}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{IconSprite}#i-{name}\"></use></svg>");

    public static string IconName(this TicketType type) => type switch
    {
        TicketType.Bug => "bug",
        TicketType.Story => "bookmark",
        _ => "square-check-big"
    };

    public static string IconName(this TicketPriority priority) => priority switch
    {
        TicketPriority.Highest => "chevrons-up",
        TicketPriority.High => "chevron-up",
        TicketPriority.Medium => "equal",
        TicketPriority.Low => "chevron-down",
        _ => "chevrons-down"
    };

    public static string Name(this TicketStatus status) => status switch
    {
        TicketStatus.ToDo => "To Do",
        TicketStatus.InProgress => "In Progress",
        TicketStatus.InReview => "In Review",
        _ => "Done"
    };

    public static string Css(this TicketStatus status) => status switch
    {
        TicketStatus.InProgress => "lozenge-progress",
        TicketStatus.InReview => "lozenge-review",
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
