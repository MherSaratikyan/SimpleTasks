using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Notifications;

public class BellModel(NotificationsClient notifications, ILogger<BellModel> logger) : PageBase
{
    public NotificationFeed? Feed { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Feed = await notifications.FeedAsync(ct);
        }
        catch (Exception exception) when (exception is not AccountRevokedException)
        {
            // The rest of the page keeps working when this service is down.
            logger.LogWarning(exception, "Could not load notifications.");
        }
    }

    public async Task<IActionResult> OnPostReadAllAsync(CancellationToken ct)
    {
        await notifications.MarkAllReadAsync(ct);
        return LocalRedirect(Request.Headers.Referer.FirstOrDefault() is { } referer && Uri.TryCreate(referer, UriKind.Absolute, out var uri) ? uri.PathAndQuery : "/");
    }

    public async Task<IActionResult> OnPostOpenAsync(Guid id, string url, CancellationToken ct)
    {
        await notifications.MarkReadAsync(id, ct);
        return LocalRedirect(Url.IsLocalUrl(url) ? url : "/");
    }
}
