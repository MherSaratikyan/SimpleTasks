using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleTasks.Data;
using SimpleTasks.Models;

namespace SimpleTasks.Pages;

public class IndexModel : PageModel
{
    private readonly TaskDbContext _db;

    public IndexModel(TaskDbContext db)
    {
        _db = db;
    }

    public List<TaskItem> Tasks { get; private set; } = new();

    [BindProperty]
    [Required(ErrorMessage = "Enter a task title.")]
    [StringLength(100, ErrorMessage = "Use 100 characters or fewer.")]
    public string Title { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        await LoadTasksAsync();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadTasksAsync();
            return Page();
        }

        _db.Tasks.Add(new TaskItem { Title = Title.Trim() });
        await _db.SaveChangesAsync();

        // Redirect after saving so refreshing the page does not add a duplicate.
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task is null)
        {
            return NotFound();
        }

        task.IsCompleted = !task.IsCompleted;
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var task = await _db.Tasks.FindAsync(id);
        if (task is null)
        {
            return NotFound();
        }

        _db.Tasks.Remove(task);
        await _db.SaveChangesAsync();
        return RedirectToPage();
    }

    private async Task LoadTasksAsync()
    {
        Tasks = await _db.Tasks
            .AsNoTracking()
            .OrderBy(task => task.IsCompleted)
            .ThenByDescending(task => task.CreatedAt)
            .ThenByDescending(task => task.Id)
            .ToListAsync();
    }
}
