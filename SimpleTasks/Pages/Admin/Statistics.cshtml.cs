using System.Text.Json;
using SimpleTasks.Contracts;
using SimpleTasks.Services;

namespace SimpleTasks.Pages.Admin;

public class StatisticsModel(WorkClient work) : PageBase
{
    public static readonly int[] Ranges = [7, 30, 90];

    public int Days { get; private set; } = 30;
    public string? Project { get; private set; }
    public List<ProjectView> Projects { get; private set; } = [];
    public StatisticsView Stats { get; private set; } = null!;

    public async Task OnGetAsync(int? days, string? project, CancellationToken ct)
    {
        Days = days is { } chosen && Ranges.Contains(chosen) ? chosen : 30;
        Projects = await work.ProjectsAsync(ct);
        Project = Projects.Any(item => item.Key == project) ? project : null;
        var timeZone = User.FindFirst(AppClaims.TimeZone)?.Value ?? "UTC";
        Stats = await work.StatisticsAsync(Days, Project, timeZone, ct);
    }

    public string Hours(double? hours) => hours switch
    {
        null => "No data",
        < 1 => $"{Math.Round(hours.Value * 60)} min",
        < 48 => $"{hours:0.#} h",
        _ => $"{hours / 24:0.#} days"
    };

    /// <summary>Chart data for the page script; serialized, so names can't break out of the script tag.</summary>
    public string ChartJson() => JsonSerializer.Serialize(new
    {
        days = Stats.Days.Select(day => day.Date.ToString("dd MMM")),
        opened = Stats.Days.Select(day => day.Opened),
        closed = Stats.Days.Select(day => day.Closed),
        toDo = Stats.Days.Select(day => day.ToDo),
        inProgress = Stats.Days.Select(day => day.InProgress),
        inReview = Stats.Days.Select(day => day.InReview),
        done = Stats.Days.Select(day => day.Done),
        current = Stats.CurrentByStatus.Select(item => new { status = item.Status.Name(), count = item.Count })
    });
}
