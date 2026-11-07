using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleTasks.Services;

namespace SimpleTasks.Pages;

public abstract class PageBase : PageModel
{
    [TempData]
    public string? Toast { get; set; }

    [TempData]
    public string? ToastError { get; set; }

    /// <summary>Runs a change, turning a service's refusal into a message on the next page.</summary>
    protected async Task<bool> AttemptAsync(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
            Toast = success;
            return true;
        }
        catch (ApiException exception)
        {
            ToastError = exception.Message;
            return false;
        }
    }
}
