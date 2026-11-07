using SimpleTasks.Notifications.Domain;
using Xunit;

namespace SimpleTasks.UnitTests;

public class ReminderRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private static DueReminderKind Next(
        DateTime? due, bool closed = false, bool assignee = true, DateTime? soonSentFor = null, DateTime? overdueSent = null) =>
        ReminderRules.Next(due, closed, assignee, soonSentFor, overdueSent, Now);

    [Fact]
    public void Nothing_without_a_due_date() => Assert.Equal(DueReminderKind.None, Next(null));

    [Fact]
    public void Nothing_more_than_a_day_ahead() => Assert.Equal(DueReminderKind.None, Next(Now.AddHours(25)));

    [Fact]
    public void Due_soon_inside_24_hours() => Assert.Equal(DueReminderKind.DueSoon, Next(Now.AddHours(23)));

    [Fact]
    public void Due_soon_goes_once_per_due_date()
    {
        var due = Now.AddHours(23);
        Assert.Equal(DueReminderKind.None, Next(due, soonSentFor: due));
        Assert.Equal(DueReminderKind.DueSoon, Next(due, soonSentFor: due.AddDays(-2)));
    }

    [Fact]
    public void Overdue_repeats_daily()
    {
        var due = Now.AddDays(-3);
        Assert.Equal(DueReminderKind.Overdue, Next(due));
        Assert.Equal(DueReminderKind.None, Next(due, overdueSent: Now.AddHours(-23)));
        Assert.Equal(DueReminderKind.Overdue, Next(due, overdueSent: Now.AddHours(-24)));
    }

    [Fact]
    public void Missed_while_stopped_catches_up_as_overdue() => Assert.Equal(DueReminderKind.Overdue, Next(Now.AddMinutes(-1)));

    [Fact]
    public void Stops_at_done_or_with_no_assignee()
    {
        Assert.Equal(DueReminderKind.None, Next(Now.AddDays(-1), closed: true));
        Assert.Equal(DueReminderKind.None, Next(Now.AddDays(-1), assignee: false));
    }

    [Fact]
    public void Shows_the_time_in_the_users_zone() =>
        Assert.StartsWith("07 Nov 2026 12:00", ReminderRules.FormatFor(Now, "UTC"));
}

public class RetryPolicyTests
{
    [Fact]
    public void Unreadable_messages_are_never_retried()
    {
        Assert.False(SimpleTasks.Messaging.RetryPolicy.ShouldRetry(new System.Text.Json.JsonException(), 1));
        Assert.False(SimpleTasks.Messaging.RetryPolicy.ShouldRetry(new FormatException(), 1));
    }

    [Fact]
    public void Other_failures_retry_up_to_the_limit()
    {
        var failure = new InvalidOperationException();
        Assert.True(SimpleTasks.Messaging.RetryPolicy.ShouldRetry(failure, 1));
        Assert.True(SimpleTasks.Messaging.RetryPolicy.ShouldRetry(failure, SimpleTasks.Messaging.Topology.DeliveryLimit - 1));
        Assert.False(SimpleTasks.Messaging.RetryPolicy.ShouldRetry(failure, SimpleTasks.Messaging.Topology.DeliveryLimit));
    }
}
