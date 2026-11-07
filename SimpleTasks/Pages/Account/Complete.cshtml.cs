using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Account;

/// <summary>Google has confirmed who this is; Identity decides whether they may come in.</summary>
public class CompleteModel(IdentityInternalClient identity) : PageBase
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken ct)
    {
        var external = await HttpContext.AuthenticateAsync("External");
        await HttpContext.SignOutAsync("External");

        var email = external.Principal?.FindFirstValue(ClaimTypes.Email);
        var subject = external.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!external.Succeeded || email is null || subject is null)
        {
            return RedirectToPage("/Account/Denied");
        }

        var user = await identity.SignInAsync(new SignInRequest(
            email,
            external.Principal!.FindFirstValue(ClaimTypes.Name) ?? email,
            external.Principal.FindFirstValue(AppClaims.Avatar),
            subject), ct);
        if (user is null)
        {
            return RedirectToPage("/Account/Denied");
        }

        await AccountCookies.SignInAsync(HttpContext, user);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
