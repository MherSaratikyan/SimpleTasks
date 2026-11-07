using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Admin;

public class UsersModel(IdentityClient identity) : PageBase
{
    public List<UserView> Users { get; private set; } = [];
    public List<InviteView> Invites { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Users = await identity.UsersAsync(ct);
        Invites = await identity.InvitesAsync(ct);
    }

    public async Task<IActionResult> OnPostInviteAsync(string email, CancellationToken ct)
    {
        await AttemptAsync(() => identity.InviteAsync(email, ct), $"Invite sent to {email}.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostActiveAsync(Guid id, bool isActive, CancellationToken ct)
    {
        await AttemptAsync(() => identity.SetActiveAsync(id, isActive, ct), isActive ? "User reactivated." : "User deactivated.");
        return RedirectToPage();
    }
}
