using System.Text;
using System.Text.RegularExpressions;
using SimpleTasks.Contracts;

namespace SimpleTasks.Work.Domain;

/// <summary>Jira-style durations: a working day is 8 hours and a working week is 5 days.</summary>
public static partial class JiraDuration
{
    public const int MinutesPerHour = 60;
    public const int MinutesPerDay = 8 * MinutesPerHour;
    public const int MinutesPerWeek = 5 * MinutesPerDay;

    [GeneratedRegex(@"^\s*(?:(\d+)\s*w)?\s*(?:(\d+)\s*d)?\s*(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        long total = 0;
        int[] units = [MinutesPerWeek, MinutesPerDay, MinutesPerHour, 1];
        for (var group = 1; group <= units.Length; group++)
        {
            if (match.Groups[group].Success)
            {
                if (!int.TryParse(match.Groups[group].Value, out var value))
                {
                    return false;
                }
                total += (long)value * units[group - 1];
            }
        }

        if (total is <= 0 or > int.MaxValue)
        {
            return false;
        }
        minutes = (int)total;
        return true;
    }

    public static string Format(int minutes)
    {
        if (minutes == 0)
        {
            return "0m";
        }

        var parts = new StringBuilder();
        void Add(int unit, char suffix)
        {
            if (minutes >= unit)
            {
                parts.Append(minutes / unit).Append(suffix).Append(' ');
                minutes %= unit;
            }
        }
        Add(MinutesPerWeek, 'w');
        Add(MinutesPerDay, 'd');
        Add(MinutesPerHour, 'h');
        Add(1, 'm');
        return parts.ToString().TrimEnd();
    }
}

public static class EstimateRules
{
    public const int WarningPercent = 80;

    public static EstimateAlertLevel LevelFor(int? estimateMinutes, int loggedMinutes)
    {
        if (estimateMinutes is not > 0)
        {
            return EstimateAlertLevel.None;
        }
        if (loggedMinutes > estimateMinutes)
        {
            return EstimateAlertLevel.Exceeded;
        }
        return loggedMinutes * 100L >= estimateMinutes.Value * (long)WarningPercent
            ? EstimateAlertLevel.Warning
            : EstimateAlertLevel.None;
    }

    /// <summary>
    /// Returns the stored level after a change and the alert to send, if any. An alert
    /// fires once per level; raising the estimate lowers the stored level so it can fire again.
    /// </summary>
    public static (EstimateAlertLevel Stored, EstimateAlertLevel? Alert) Evaluate(
        EstimateAlertLevel stored, int? estimateMinutes, int loggedMinutes)
    {
        var current = LevelFor(estimateMinutes, loggedMinutes);
        return current > stored ? (current, current) : (current, null);
    }
}

public static class Ranking
{
    public const double Gap = 1024;

    /// <summary>Picks a board position between two neighbours, either of which may be missing.</summary>
    public static double Between(double? previous, double? next) => (previous, next) switch
    {
        (null, null) => Gap,
        ({ } before, null) => before + Gap,
        (null, { } after) => after - Gap,
        ({ } before, { } after) => (before + after) / 2
    };
}

public static class ProjectAccess
{
    public static bool CanView(ProjectRole? role) => role is not null;
    public static bool CanEdit(ProjectRole? role) => role is ProjectRole.Member or ProjectRole.Admin;
    public static bool CanAdminister(ProjectRole? role) => role is ProjectRole.Admin;

    /// <summary>Site Admins act as Project Admin everywhere; everyone else needs a membership.</summary>
    public static ProjectRole? EffectiveRole(ProjectRole? membership, bool isSiteAdmin) =>
        isSiteAdmin ? ProjectRole.Admin : membership;
}

public static partial class TicketKey
{
    [GeneratedRegex("^[A-Z][A-Z0-9]{1,9}$")]
    private static partial Regex ProjectKeyPattern();

    public static bool IsValidProjectKey(string key) => ProjectKeyPattern().IsMatch(key);

    public static string Format(string projectKey, int number) => $"{projectKey}-{number}";

    public static bool TryParse(string key, out string projectKey, out int number)
    {
        projectKey = string.Empty;
        number = 0;
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !int.TryParse(key.AsSpan(dash + 1), out number))
        {
            return false;
        }
        projectKey = key[..dash].ToUpperInvariant();
        return true;
    }
}
