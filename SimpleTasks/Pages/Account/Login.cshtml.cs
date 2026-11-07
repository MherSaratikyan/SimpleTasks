using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Account;

public class LoginModel(
    IdentityInternalClient identity,
    IOptions<GoogleSignInOptions> google,
    IWebHostEnvironment environment) : PageBase
{
    public string ReturnUrl { get; private set; } = "/";
    public bool GoogleEnabled => google.Value.Enabled;
    public List<DevUser> DevUsers { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = Safe(returnUrl);
        if (User.Identity?.IsAuthenticated == true)
        {
            return LocalRedirect(ReturnUrl);
        }
        if (environment.IsDevelopment())
        {
            DevUsers = await identity.GetDevUsersAsync(ct);
        }
        return Page();
    }

    public IActionResult OnPostGoogle(string? returnUrl)
    {
        if (!GoogleEnabled)
        {
            return RedirectToPage();
        }
        var complete = Url.Page("/Account/Complete", new { returnUrl = Safe(returnUrl) });
        return Challenge(new AuthenticationProperties { RedirectUri = complete }, GoogleDefaults.AuthenticationScheme);
    }

    public async Task<IActionResult> OnPostDevAsync(string email, string? returnUrl, CancellationToken ct)
    {
        if (!environment.IsDevelopment())
        {
            return NotFound();
        }
        var user = await identity.SignInAsync(new SignInRequest(email, email.Split('@')[0], null, null), ct);
        if (user is null)
        {
            return RedirectToPage("/Account/Denied");
        }
        await AccountCookies.SignInAsync(HttpContext, user);
        return LocalRedirect(Safe(returnUrl));
    }

    private string Safe(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
}
