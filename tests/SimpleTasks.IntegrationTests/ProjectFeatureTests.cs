using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimpleTasks.Contracts;
using Xunit;

namespace SimpleTasks.IntegrationTests;

public class ProjectFeatureTests(SystemFixture system)
{
    private sealed record Created(string Key);
    private sealed record Feed(int Unread, List<Item> Items);
    private sealed record Item(string Title);
    private sealed record UserRef(Guid Id, string DisplayName);
    private sealed record Card(string Key, TicketStatus Status, UserRef? QaOwner);
    private sealed record History(int Kind, string? From, string? To);
    private sealed record Detail(Card Card, List<History> Activity, UserRef? AssignedBy, UserRef? CompletedBy, DateTime? CompletedAt);
    private sealed record Day(int Opened, int Closed, int Done);
    private sealed record Stats(int Opened, int Closed, int Reopened, int CurrentlyOpen, double? AverageHoursToComplete, List<Day> Days);

    private static async Task<string> ProjectAsync(TestUser admin)
    {
        var key = $"F{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        (await admin.Work.PostAsJsonAsync("/api/projects", new { key, name = $"Project {key}" })).EnsureSuccessStatusCode();
        return key;
    }

    private static async Task<string> TicketAsync(TestUser user, string project, Guid? assigneeId = null, Guid? qaOwnerId = null)
    {
        var response = await user.Work.PostAsJsonAsync($"/api/projects/{project}/tickets", new
        {
            title = "Feature ticket", type = TicketType.Task, priority = TicketPriority.Medium, assigneeId, qaOwnerId
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>())!.Key;
    }

    private async Task<TestUser> MemberAsync(TestUser admin, string project, ProjectRole role = ProjectRole.Member)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        (await admin.Work.PostAsJsonAsync($"/api/projects/{project}/invites", new { email, role })).EnsureSuccessStatusCode();
        var user = await system.AcceptInviteAsync(email);
        await SystemFixture.WaitUntilAsync(async () => (await user.Work.GetAsync($"/api/projects/{project}")).IsSuccessStatusCode,
            "the membership to reach Work");
        return user;
    }

    private static Task<HttpResponseMessage> MoveAsync(TestUser user, string ticket, TicketStatus status) =>
        user.Work.PostAsJsonAsync($"/api/tickets/{ticket}/move", new { status });

    [Fact]
    public async Task Deleting_a_project_needs_a_site_admin_and_the_typed_key_and_removes_everything()
    {
        var admin = await system.AdminAsync();
        var project = await ProjectAsync(admin);
        var projectAdmin = await MemberAsync(admin, project, ProjectRole.Admin);
        var ticket = await TicketAsync(admin, project);

        var pendingEmail = $"{Guid.NewGuid():N}@example.test";
        (await admin.Work.PostAsJsonAsync($"/api/projects/{project}/invites", new { email = pendingEmail, role = ProjectRole.Member })).EnsureSuccessStatusCode();
        var inviteToken = await system.LinkTokenAsync(pendingEmail, "You're invited");

        await SystemFixture.WaitUntilAsync(async () =>
            (await admin.Notifications.PostAsJsonAsync("/api/reminders", new { ticketKey = ticket, remindAt = DateTime.UtcNow.AddDays(1) })).IsSuccessStatusCode,
            "the ticket to reach Notifications");

        Assert.Equal(HttpStatusCode.Forbidden, (await projectAdmin.Work.DeleteAsync($"/api/projects/{project}?confirm={project}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Work.DeleteAsync($"/api/projects/{project}?confirm=WRONG")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Work.DeleteAsync($"/api/projects/{project}?confirm={project}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Work.GetAsync($"/api/projects/{project}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Work.GetAsync($"/api/tickets/{ticket}")).StatusCode);

        await SystemFixture.WaitUntilAsync(async () =>
            (await system.InternalClient().GetAsync($"/internal/password-token?token={Uri.EscapeDataString(inviteToken)}")).StatusCode == HttpStatusCode.NotFound,
            "the pending invite to be cancelled");
        await SystemFixture.WaitUntilAsync(async () =>
            (await admin.Notifications.GetFromJsonAsync<List<JsonElement>>($"/api/reminders?ticketKey={ticket}"))!.Count == 0,
            "the reminder to be removed");
    }

    [Fact]
    public async Task The_qa_owner_is_asked_to_review_when_a_ticket_reaches_in_review()
    {
        var admin = await system.AdminAsync();
        var project = await ProjectAsync(admin);
        var reviewer = await MemberAsync(admin, project);
        var ticket = await TicketAsync(admin, project, qaOwnerId: reviewer.Id);

        (await MoveAsync(admin, ticket, TicketStatus.InReview)).EnsureSuccessStatusCode();

        await SystemFixture.WaitUntilAsync(async () =>
            (await reviewer.Notifications.GetFromJsonAsync<Feed>("/api/notifications"))!.Items.Any(item => item.Title == $"{ticket} is ready for your review"),
            "the review request");
        var detail = await admin.Work.GetFromJsonAsync<Detail>($"/api/tickets/{ticket}");
        Assert.Equal(reviewer.Id, detail!.Card.QaOwner!.Id);
    }

    [Fact]
    public async Task The_qa_owner_must_be_a_project_member()
    {
        var admin = await system.AdminAsync();
        var project = await ProjectAsync(admin);
        var response = await admin.Work.PostAsJsonAsync($"/api/projects/{project}/tickets", new
        {
            title = "Bad QA", type = TicketType.Task, priority = TicketPriority.Medium, qaOwnerId = Guid.NewGuid()
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Done_by_and_completion_time_follow_the_latest_completion_and_history_records_each_move()
    {
        var admin = await system.AdminAsync();
        var project = await ProjectAsync(admin);
        var member = await MemberAsync(admin, project);
        var ticket = await TicketAsync(admin, project, assigneeId: member.Id);

        var assigned = await admin.Work.GetFromJsonAsync<Detail>($"/api/tickets/{ticket}");
        Assert.Equal(admin.Id, assigned!.AssignedBy!.Id);

        (await MoveAsync(member, ticket, TicketStatus.Done)).EnsureSuccessStatusCode();
        var done = await admin.Work.GetFromJsonAsync<Detail>($"/api/tickets/{ticket}");
        Assert.Equal(member.Id, done!.CompletedBy!.Id);
        Assert.NotNull(done.CompletedAt);

        (await MoveAsync(admin, ticket, TicketStatus.InProgress)).EnsureSuccessStatusCode();
        var reopened = await admin.Work.GetFromJsonAsync<Detail>($"/api/tickets/{ticket}");
        Assert.Null(reopened!.CompletedBy);
        Assert.Null(reopened.CompletedAt);

        var moves = reopened.Activity.Where(entry => entry.Kind == 2).ToList();
        Assert.Contains(moves, entry => entry is { From: "To Do", To: "Done" });
        Assert.Contains(moves, entry => entry is { From: "Done", To: "In Progress" });
    }

    [Fact]
    public async Task Statistics_count_this_projects_activity_and_are_for_site_admins_only()
    {
        var admin = await system.AdminAsync();
        var project = await ProjectAsync(admin);
        var member = await MemberAsync(admin, project);
        var first = await TicketAsync(admin, project);
        await TicketAsync(admin, project);
        (await MoveAsync(admin, first, TicketStatus.Done)).EnsureSuccessStatusCode();
        (await MoveAsync(admin, first, TicketStatus.InProgress)).EnsureSuccessStatusCode();
        (await MoveAsync(admin, first, TicketStatus.Done)).EnsureSuccessStatusCode();

        var stats = await admin.Work.GetFromJsonAsync<Stats>($"/api/statistics?days=7&project={project}&tz=UTC");

        Assert.Equal(2, stats!.Opened);
        Assert.Equal(2, stats.Closed);
        Assert.Equal(1, stats.Reopened);
        Assert.Equal(1, stats.CurrentlyOpen);
        Assert.Equal(7, stats.Days.Count);
        Assert.Equal(2, stats.Days[^1].Opened);
        Assert.NotNull(stats.AverageHoursToComplete);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.Work.GetAsync("/api/statistics")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Work.GetAsync("/api/statistics?days=5")).StatusCode);
    }
}
