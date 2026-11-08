using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Account;

public class LoginModel(IdentityInternalClient identity, IWebHostEnvironment environment) : PageBase
{
    public string ReturnUrl { get; private set; } = "/";
    public string? Email { get; private set; }
    public string? Error { get; private set; }
    public List<DevUser> DevUsers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = Safe(returnUrl);
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl);
        }
        await LoadDevUsersAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string email, string password, string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = Safe(returnUrl);
        var (user, error) = await identity.LoginAsync(email, password, ct);
        if (user is null)
        {
            Email = email;
            Error = error;
            await LoadDevUsersAsync(ct);
            return Page();
        }
        await AccountCookies.SignInAsync(HttpContext, user);
        return LocalRedirect(ReturnUrl);
    }

    public async Task<IActionResult> OnPostDevAsync(string email, string? returnUrl, CancellationToken ct)
    {
        if (!DevSignInAllowed)
        {
            return NotFound();
        }
        var user = await identity.DevSignInAsync(email, ct);
        if (user is null)
        {
            return RedirectToPage("/Account/Denied");
        }
        await AccountCookies.SignInAsync(HttpContext, user);
        return LocalRedirect(Safe(returnUrl));
    }

    /// <summary>
    /// The passwordless test accounts include a Site Admin, so they work only in Development
    /// and only from this computer, never for someone else on the network.
    /// </summary>
    private bool DevSignInAllowed =>
        environment.IsDevelopment() && HttpContext.Connection.RemoteIpAddress is { } address && System.Net.IPAddress.IsLoopback(address);

    private async Task LoadDevUsersAsync(CancellationToken ct)
    {
        if (DevSignInAllowed)
        {
            DevUsers = await identity.GetDevUsersAsync(ct);
        }
    }

    private string Safe(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
