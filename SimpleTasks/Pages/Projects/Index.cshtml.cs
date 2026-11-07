using Microsoft.AspNetCore.Mvc;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Projects;

public class IndexModel(WorkClient work) : PageBase
{
    public List<ProjectView> Projects { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct) => Projects = await work.ProjectsAsync(ct);

    public async Task<IActionResult> OnPostAsync(string name, string key, CancellationToken ct)
    {
        await AttemptAsync(() => work.CreateProjectAsync(key, name, null, ct), $"Created {name}.");
        return RedirectToPage();
    }
}
