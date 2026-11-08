using SimpleTasks.Contracts;
using SimpleTasks.Work.Data;
using SimpleTasks.Work.Domain;
using Xunit;

namespace SimpleTasks.UnitTests;

public class TicketLifecycleTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();

    [Fact]
    public void Finishing_records_who_and_when()
    {
        var ticket = new Ticket { Status = TicketStatus.InReview };

        TicketLifecycle.ApplyStatus(ticket, TicketStatus.Done, Alice, Now);

        Assert.Equal((TicketStatus.Done, Alice, Now), (ticket.Status, ticket.CompletedById!.Value, ticket.CompletedAt!.Value));
    }

    [Fact]
    public void Reopening_clears_completion_and_finishing_again_records_the_new_one()
    {
        var ticket = new Ticket { Status = TicketStatus.InReview };
        TicketLifecycle.ApplyStatus(ticket, TicketStatus.Done, Alice, Now);

        TicketLifecycle.ApplyStatus(ticket, TicketStatus.InProgress, Bob, Now.AddHours(1));
        Assert.Null(ticket.CompletedAt);
        Assert.Null(ticket.CompletedById);

        TicketLifecycle.ApplyStatus(ticket, TicketStatus.Done, Bob, Now.AddHours(2));
        Assert.Equal((Bob, Now.AddHours(2)), (ticket.CompletedById!.Value, ticket.CompletedAt!.Value));
    }

    [Fact]
    public void Moving_between_open_statuses_leaves_completion_alone()
    {
        var ticket = new Ticket { Status = TicketStatus.ToDo };
        TicketLifecycle.ApplyStatus(ticket, TicketStatus.InProgress, Alice, Now);
        Assert.Null(ticket.CompletedAt);
    }

    [Fact]
    public void Assigning_records_who_assigned_and_unassigning_clears_it()
    {
        var ticket = new Ticket();
        TicketLifecycle.ApplyAssignee(ticket, Bob, Alice, Now);
        Assert.Equal((Bob, Alice, Now), (ticket.AssigneeId!.Value, ticket.AssignedById!.Value, ticket.AssignedAt!.Value));

        TicketLifecycle.ApplyAssignee(ticket, null, Alice, Now);
        Assert.Null(ticket.AssignedById);
        Assert.Null(ticket.AssignedAt);
    }

    [Theory]
    [InlineData(TicketStatus.InProgress, TicketStatus.InReview, true, true, true)]
    [InlineData(TicketStatus.InProgress, TicketStatus.InReview, false, false, false)]
    [InlineData(TicketStatus.InReview, TicketStatus.InReview, true, true, false)]
    [InlineData(TicketStatus.InReview, TicketStatus.InReview, false, true, true)]
    [InlineData(TicketStatus.InReview, TicketStatus.Done, true, true, false)]
    public void Review_is_requested_on_entering_review_or_getting_a_new_qa_owner(
        TicketStatus before, TicketStatus after, bool hadQa, bool hasQa, bool expected)
    {
        Guid? qaBefore = hadQa ? Alice : null;
        Guid? qaAfter = hasQa ? Alice : null;
        Assert.Equal(expected, TicketLifecycle.ShouldRequestReview(before, after, qaBefore, qaAfter));
    }

    [Fact]
    public void A_changed_qa_owner_in_review_is_asked_too() =>
        Assert.True(TicketLifecycle.ShouldRequestReview(TicketStatus.InReview, TicketStatus.InReview, Alice, Bob));
}

public class StatisticsTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly DateOnly First = new(2026, 11, 1);
    private static readonly DateOnly Last = new(2026, 11, 7);

    private static DateTime At(int day, int hour = 12) => new(2026, 11, day, hour, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Counts_opened_closed_reopened_and_open_within_the_range()
    {
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();
        var tickets = new List<StatsTicket>
        {
            new(t1, ProjectA, At(2), At(4), TicketStatus.Done, Alice, Alice),
            new(t2, ProjectA, At(3), null, TicketStatus.InProgress, Alice),
            new(t3, ProjectB, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), null, TicketStatus.ToDo, null)
        };
        var changes = new List<StatsStatusChange>
        {
            new(t1, Alice, At(3), TicketStatus.ToDo, TicketStatus.InProgress),
            new(t1, Alice, At(4), TicketStatus.InProgress, TicketStatus.Done),
            new(t2, Alice, At(5), TicketStatus.Done, TicketStatus.InProgress)
        };

        var result = Statistics.Calculate(tickets, changes, First, Last, TimeZoneInfo.Utc);

        Assert.Equal(2, result.Opened);
        Assert.Equal(1, result.Closed);
        Assert.Equal(1, result.Reopened);
        Assert.Equal(2, result.CurrentlyOpen);
        Assert.Equal(48, result.AverageHoursToComplete);
        Assert.Equal(7, result.Days.Count);
        Assert.Equal(1, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 4)).Done);
        Assert.Equal(1, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 3)).InProgress);

        var alice = Assert.Single(result.People);
        Assert.Equal((1, 1), (alice.Completed, alice.OpenAssigned));
        Assert.Equal(1, result.Projects.Single(project => project.ProjectId == ProjectB).Open);
    }

    [Fact]
    public void Days_follow_the_viewers_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus4", TimeSpan.FromHours(4), "UTC+4", "UTC+4");
        var late = new DateTime(2026, 11, 3, 22, 0, 0, DateTimeKind.Utc);
        var tickets = new List<StatsTicket> { new(Guid.NewGuid(), ProjectA, late, null, TicketStatus.ToDo, null) };

        var result = Statistics.Calculate(tickets, [], First, Last, zone);

        Assert.Equal(1, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 4)).Opened);
        Assert.Equal(0, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 3)).Opened);
    }

    [Fact]
    public void A_ticket_closed_several_times_counts_once_and_a_new_ticket_is_never_closed()
    {
        // What happened with BUGGY-1 and BUGGY-2: one ticket dragged in and out of Done three times, one just created.
        var bouncing = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var tickets = new List<StatsTicket>
        {
            new(bouncing, ProjectA, At(5, 9), At(5, 16), TicketStatus.Done, Alice, Alice),
            new(fresh, ProjectA, At(5, 15), null, TicketStatus.ToDo, Alice)
        };
        var changes = new List<StatsStatusChange>
        {
            new(bouncing, Alice, At(5, 10), TicketStatus.InReview, TicketStatus.Done),
            new(bouncing, Alice, At(5, 11), TicketStatus.Done, TicketStatus.InReview),
            new(bouncing, Alice, At(5, 12), TicketStatus.InReview, TicketStatus.Done),
            new(bouncing, Alice, At(5, 13), TicketStatus.Done, TicketStatus.InProgress),
            new(bouncing, Alice, At(5, 16), TicketStatus.InProgress, TicketStatus.Done)
        };

        var result = Statistics.Calculate(tickets, changes, First, Last, TimeZoneInfo.Utc);

        Assert.Equal((2, 1, 1, 1), (result.Opened, result.Closed, result.Reopened, result.CurrentlyOpen));
        Assert.Equal(1, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 5)).Closed);
        Assert.Equal(3, result.Days.Single(day => day.Date == new DateOnly(2026, 11, 5)).Done);
        Assert.Equal(1, result.People.Single().Completed);
    }

    [Fact]
    public void No_completions_means_no_average() =>
        Assert.Null(Statistics.Calculate([], [], First, Last, TimeZoneInfo.Utc).AverageHoursToComplete);
}
