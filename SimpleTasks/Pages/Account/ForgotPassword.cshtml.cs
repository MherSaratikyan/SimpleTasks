using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Account;

public class ForgotPasswordModel(IdentityInternalClient identity) : PageBase
{
    public bool Sent { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string email, CancellationToken ct)
    {
        // The same reply whether or not the email has an account.
        await identity.RequestPasswordResetAsync(email, ct);
        Sent = true;
        return Page();
    }
}
