using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Projects;

public class SettingsModel(WorkClient work) : PageBase
{
    public ProjectView Project { get; private set; } = null!;
    public List<MemberView> Members { get; private set; } = [];
    public List<LabelView> Labels { get; private set; } = [];
    public List<TicketCard> Deleted { get; private set; } = [];
    public bool CanAdminister => Project.Role == ProjectRole.Admin;

    public async Task OnGetAsync(string key, CancellationToken ct)
    {
        Project = await work.ProjectAsync(key, ct);
        Members = await work.MembersAsync(key, ct);
        Labels = await work.LabelsAsync(key, ct);
        if (CanAdminister)
        {
            Deleted = await work.DeletedTicketsAsync(key, ct);
        }
    }

    public async Task<IActionResult> OnPostInviteAsync(string key, string email, ProjectRole role, CancellationToken ct)
    {
        await AttemptAsync(() => work.InviteMemberAsync(key, email, role, ct), $"Added or invited {email}. It may take a moment to appear.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRoleAsync(string key, Guid userId, ProjectRole role, CancellationToken ct)
    {
        await AttemptAsync(() => work.SetRoleAsync(key, userId, role, ct), "Role updated.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string key, Guid userId, CancellationToken ct)
    {
        await AttemptAsync(() => work.RemoveMemberAsync(key, userId, ct), "Removed from the project.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostLabelAsync(string key, string name, string color, CancellationToken ct)
    {
        await AttemptAsync(() => work.CreateLabelAsync(key, name, color, ct));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteLabelAsync(string key, Guid id, CancellationToken ct)
    {
        await AttemptAsync(() => work.DeleteLabelAsync(key, id, ct));
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreAsync(string ticketKey, CancellationToken ct)
    {
        await AttemptAsync(() => work.RestoreTicketAsync(ticketKey, ct), $"Restored {ticketKey}.");
        return RedirectToPage();
    }
}
