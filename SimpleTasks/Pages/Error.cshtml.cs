using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SimpleTasks.Pages;

public class ErrorModel : PageModel
{
    public string Heading { get; private set; } = "Something isn't working";
    public string Message { get; private set; } = "Part of Simple Tasks is unavailable right now. Please try again in a moment.";

    public void OnGet(int? code) => Describe(code);

    public void OnPost(int? code) => Describe(code);

    private void Describe(int? code)
    {
        if (code == 404)
        {
            Heading = "We can't find that";
            Message = "It may have been deleted, or you may not have access to it.";
        }
        else if (code == 403)
        {
            Heading = "You don't have access";
            Message = "Ask a Project Admin if you think you should.";
        }
    }
}
