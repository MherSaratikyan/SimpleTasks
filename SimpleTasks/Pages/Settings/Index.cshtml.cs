using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Settings;

public class IndexModel(IdentityClient identity, NotificationsClient notifications) : PageBase
{
    public IReadOnlyList<TimeZoneInfo> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones();
    public string TimeZoneId { get; private set; } = "UTC";
    public List<PreferenceView> Preferences { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        TimeZoneId = User.FindFirst(AppClaims.TimeZone)?.Value ?? "UTC";
        Preferences = await notifications.PreferencesAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(string timeZoneId, List<NotificationType> emailTypes, CancellationToken ct)
    {
        var saved = await AttemptAsync(async () =>
        {
            await identity.SetTimeZoneAsync(timeZoneId, ct);
            await notifications.SavePreferencesAsync(
                Enum.GetValues<NotificationType>().Select(type => new PreferenceView(type, emailTypes.Contains(type))).ToList(), ct);
        }, "Settings saved.");
        if (saved)
        {
            await AccountCookies.UpdateTimeZoneAsync(HttpContext, timeZoneId);
        }
        return RedirectToPage();
    }
}
