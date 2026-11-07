namespace SimpleTasks.Notifications.Domain;

public enum DueReminderKind { None, DueSoon, Overdue }

/// <summary>
/// Decides from saved state rather than timers, so reminders missed
/// while the app was stopped go out on the next check after a restart.
/// </summary>
public static class ReminderRules
{
    public static readonly TimeSpan DueSoonWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan OverdueInterval = TimeSpan.FromHours(24);

    public static DueReminderKind Next(
        DateTime? dueAt, bool isClosed, bool hasAssignee, DateTime? dueSoonSentFor, DateTime? lastOverdueSentAt, DateTime now)
    {
        if (dueAt is not { } due || isClosed || !hasAssignee)
        {
            return DueReminderKind.None;
        }
        if (now >= due)
        {
            return lastOverdueSentAt is { } last && now - last < OverdueInterval
                ? DueReminderKind.None
                : DueReminderKind.Overdue;
        }
        return now >= due - DueSoonWindow && dueSoonSentFor != due
            ? DueReminderKind.DueSoon
            : DueReminderKind.None;
    }

    public static string FormatFor(DateTime utc, string? timeZoneId)
    {
        var zone = timeZoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found)
            ? found
            : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return $"{local:dd MMM yyyy HH:mm} ({zone.Id})";
    }
}
