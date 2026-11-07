using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Projects;

/// <summary>Shared by the board and backlog: loads a project's tickets through the filter bar.</summary>
public abstract class TicketListPage(WorkClient work) : PageBase
{
    protected WorkClient Work { get; } = work;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Assignee { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Label { get; set; }
    [BindProperty(SupportsGet = true)] public TicketType? Type { get; set; }
    [BindProperty(SupportsGet = true)] public TicketPriority? Priority { get; set; }

    public ProjectView Project { get; private set; } = null!;
    public List<TicketCard> Tickets { get; private set; } = [];
    public List<MemberView> Members { get; private set; } = [];
    public List<LabelView> Labels { get; private set; } = [];
    public bool CanEdit => Project.Role >= ProjectRole.Member;

    public async Task OnGetAsync(string key, CancellationToken ct)
    {
        Project = await Work.ProjectAsync(key, ct);
        Tickets = await Work.TicketsAsync(key, new TicketFilter(Q, Assignee, Label, Type, Priority), ct);
        Members = await Work.MembersAsync(key, ct);
        Labels = await Work.LabelsAsync(key, ct);
    }
}
