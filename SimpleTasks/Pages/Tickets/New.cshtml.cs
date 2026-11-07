using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Tickets;

public sealed record TicketFieldsView(
    string? Title, string? Description, TicketType Type, TicketPriority Priority, Guid? AssigneeId,
    string? Estimate, DateTime? DueAt, IReadOnlyCollection<Guid> SelectedLabels,
    IReadOnlyList<MemberView> Members, IReadOnlyList<LabelView> Labels);

public sealed class TicketForm
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TicketType Type { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public Guid? AssigneeId { get; set; }
    public string? Estimate { get; set; }
    public DateTime? DueAt { get; set; }
    public List<Guid> LabelIds { get; set; } = [];

    public SaveTicket ToRequest(UserTime time) =>
        new(Title, Description, Type, Priority, AssigneeId, Estimate, time.FromInput(DueAt), LabelIds);
}

public class NewModel(WorkClient work, UserTime time) : PageBase
{
    [BindProperty(SupportsGet = true)] public bool Panel { get; set; }
    [BindProperty] public TicketForm Form { get; set; } = new();

    public ProjectView Project { get; private set; } = null!;
    public List<MemberView> Members { get; private set; } = [];
    public List<LabelView> Labels { get; private set; } = [];

    public async Task OnGetAsync(string key, CancellationToken ct)
    {
        Project = await work.ProjectAsync(key, ct);
        Members = await work.MembersAsync(key, ct);
        Labels = await work.LabelsAsync(key, ct);
    }

    public async Task<IActionResult> OnPostAsync(string key, CancellationToken ct)
    {
        string? created = null;
        var saved = await AttemptAsync(async () => created = await work.CreateTicketAsync(key, Form.ToRequest(time), ct));
        return saved
            ? Redirect($"/tickets/{created}{(Panel ? "?panel=true" : string.Empty)}")
            : RedirectToPage(new { key, panel = Panel });
    }
}
