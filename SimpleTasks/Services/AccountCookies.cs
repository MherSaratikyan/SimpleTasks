using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SimpleTasks.Services;

public static class AppClaims
{
    public const string SiteAdmin = "site_admin";
    public const string TimeZone = "time_zone";
    public const string Avatar = "avatar";
}

public static class AccountCookies
{
    public static Task SignInAsync(HttpContext http, SignInResponse user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(AppClaims.TimeZone, user.TimeZoneId)
        };
        if (user.AvatarUrl is not null)
        {
            claims.Add(new Claim(AppClaims.Avatar, user.AvatarUrl));
        }
        if (user.IsSiteAdmin)
        {
            claims.Add(new Claim(AppClaims.SiteAdmin, "true"));
        }
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Reissues the cookie so a changed time zone applies straight away.</summary>
    public static Task UpdateTimeZoneAsync(HttpContext http, string timeZoneId)
    {
        var user = http.User;
        return SignInAsync(http, new SignInResponse(
            Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!),
            user.FindFirstValue(ClaimTypes.Email)!,
            user.FindFirstValue(ClaimTypes.Name)!,
            user.FindFirstValue(AppClaims.Avatar),
            timeZoneId,
            user.HasClaim(AppClaims.SiteAdmin, "true")));
    }
}

/// <summary>Shows and reads times in the signed-in user's time zone; everything is stored in UTC.</summary>
public sealed class UserTime(IHttpContextAccessor accessor)
{
    private TimeZoneInfo Zone =>
        accessor.HttpContext?.User.FindFirstValue(AppClaims.TimeZone) is { } id
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
            ? zone
            : TimeZoneInfo.Utc;

    public string Show(DateTime utc) => Local(utc).ToString("dd MMM yyyy, HH:mm");

    public string ShowDate(DateTime utc) => Local(utc).ToString("dd MMM yyyy");

    public string ToInput(DateTime? utc) => utc is { } value ? Local(value).ToString("yyyy-MM-ddTHH:mm") : string.Empty;

    public DateTime? FromInput(DateTime? local) =>
        local is { } value
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), Zone)
            : null;

    private DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}

public sealed class GoogleSignInOptions
{
    public bool Enabled { get; set; }
}
