using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Tickets;

public class DetailModel(WorkClient work, NotificationsClient notifications, UserTime time, ILogger<DetailModel> logger) : PageBase
{
    [BindProperty(SupportsGet = true)] public bool Panel { get; set; }
    [BindProperty] public TicketForm Form { get; set; } = new();

    public TicketDetail Ticket { get; private set; } = null!;
    public List<MemberView> Members { get; private set; } = [];
    public List<LabelView> Labels { get; private set; } = [];
    public List<ReminderView> Reminders { get; private set; } = [];

    public async Task OnGetAsync(string key, CancellationToken ct)
    {
        Ticket = await work.TicketAsync(key, ct);
        Members = await work.MembersAsync(Ticket.ProjectKey, ct);
        Labels = await work.LabelsAsync(Ticket.ProjectKey, ct);
        try
        {
            Reminders = await notifications.RemindersAsync(key, ct);
        }
        catch (Exception exception) when (exception is not AccountRevokedException)
        {
            // The ticket stays usable when Notifications is down.
            logger.LogWarning(exception, "Could not load reminders for {Key}.", key);
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(string key, TicketStatus status, CancellationToken ct)
    {
        await AttemptAsync(async () =>
        {
            await work.UpdateTicketAsync(key, Form.ToRequest(time), ct);
            var current = await work.TicketAsync(key, ct);
            if (current.Card.Status != status)
            {
                await work.MoveTicketAsync(key, status, null, null, ct);
            }
        }, "Saved.");
        return Back(key);
    }

    public async Task<IActionResult> OnPostCommentAsync(string key, string body, CancellationToken ct)
    {
        await AttemptAsync(() => work.CommentAsync(key, body, ct));
        return Back(key);
    }

    public async Task<IActionResult> OnPostLogWorkAsync(string key, string time, string? note, CancellationToken ct)
    {
        await AttemptAsync(() => work.LogWorkAsync(key, time, note, ct), "Work logged.");
        return Back(key);
    }

    public async Task<IActionResult> OnPostReminderAsync(string key, DateTime remindAt, string? note, CancellationToken ct)
    {
        await AttemptAsync(async () =>
        {
            // Notifications can't see project access, so reading the ticket here proves it.
            await work.TicketAsync(key, ct);
            await notifications.AddReminderAsync(key, time.FromInput(remindAt)!.Value, note, ct);
        }, "Reminder set.");
        return Back(key);
    }

    public async Task<IActionResult> OnPostDeleteReminderAsync(string key, Guid id, CancellationToken ct)
    {
        await AttemptAsync(() => notifications.DeleteReminderAsync(id, ct));
        return Back(key);
    }

    public async Task<IActionResult> OnPostDeleteAsync(string key, CancellationToken ct)
    {
        var ticket = await work.TicketAsync(key, ct);
        await AttemptAsync(() => work.DeleteTicketAsync(key, ct), $"Deleted {key}.");
        return Redirect($"/projects/{ticket.ProjectKey}/board");
    }

    private RedirectResult Back(string key) => Redirect($"/tickets/{key}{(Panel ? "?panel=true" : string.Empty)}");
}
