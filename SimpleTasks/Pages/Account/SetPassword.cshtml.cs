using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Account;

/// <summary>Where invite and reset links land. Setting the password also logs the person in.</summary>
public class SetPasswordModel(IdentityInternalClient identity) : PageBase
{
    public string Token { get; private set; } = string.Empty;
    public PasswordLink? Link { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Error { get; private set; }
    public bool IsInvite => Link?.Kind == "invite";

    public async Task OnGetAsync(string? token, CancellationToken ct) => await LoadAsync(token, ct);

    public async Task<IActionResult> OnPostAsync(string token, string password, string confirm, string? displayName, CancellationToken ct)
    {
        await LoadAsync(token, ct);
        if (Link is null)
        {
            return Page();
        }

        DisplayName = displayName;
        if (password != confirm)
        {
            Error = "The passwords don't match.";
            return Page();
        }

        var (user, error) = await identity.SetPasswordAsync(token, password, displayName, ct);
        if (user is null)
        {
            Error = error;
            return Page();
        }
        await AccountCookies.SignInAsync(HttpContext, user);
        return LocalRedirect("/");
    }

    private async Task LoadAsync(string? token, CancellationToken ct)
    {
        Token = token ?? string.Empty;
        Link = Token.Length is > 0 and <= 100 ? await identity.CheckPasswordLinkAsync(Token, ct) : null;
    }
}
