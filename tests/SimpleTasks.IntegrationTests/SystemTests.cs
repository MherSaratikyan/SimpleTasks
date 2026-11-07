using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.SqlClient;
using RabbitMQ.Client;
using SimpleTasks.Contracts;
using Xunit;

namespace SimpleTasks.IntegrationTests;

public class SystemTests(SystemFixture system)
{
    private sealed record Feed(int Unread, List<Item> Items);
    private sealed record Item(string Title, string Body);
    private sealed record Project(Guid Id, string Key, string Name, ProjectRole Role);
    private sealed record Created(string Key);
    private sealed record Card(string Key, string Title, TicketStatus Status);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private Task<TestUser> AdminAsync() => system.SignInAsync(SystemFixture.FirstAdminEmail);

    private static async Task<string> CreateProjectAsync(TestUser admin)
    {
        var key = Unique("P");
        var response = await admin.Work.PostAsJsonAsync("/api/projects", new { key, name = $"Project {key}" });
        response.EnsureSuccessStatusCode();
        return key;
    }

    private static async Task<string> CreateTicketAsync(
        TestUser user, string projectKey, string title, Guid? assigneeId = null, string? estimate = null, DateTime? dueAt = null)
    {
        var response = await user.Work.PostAsJsonAsync($"/api/projects/{projectKey}/tickets", new
        {
            title, type = TicketType.Task, priority = TicketPriority.Medium, assigneeId, estimate, dueAt
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>())!.Key;
    }

    private static async Task<List<string>> BellAsync(TestUser user)
    {
        var feed = await user.Notifications.GetFromJsonAsync<Feed>("/api/notifications?take=100");
        return feed!.Items.Select(item => item.Title).ToList();
    }

    private async Task<TestUser> InviteToProjectAsync(TestUser admin, string projectKey, ProjectRole role)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var invited = await admin.Work.PostAsJsonAsync($"/api/projects/{projectKey}/invites", new { email, role });
        Assert.Equal(HttpStatusCode.Accepted, invited.StatusCode);

        await SystemFixture.WaitUntilAsync(
            async () => (await system.TrySignInAsync(email)).IsSuccessStatusCode, "the invite to reach Identity");
        var user = await system.SignInAsync(email);
        await SystemFixture.WaitUntilAsync(
            async () => (await user.Work.GetAsync($"/api/projects/{projectKey}")).IsSuccessStatusCode, "the membership to reach Work");
        return user;
    }

    [Fact]
    public async Task Only_invited_people_get_in()
    {
        var stranger = await system.TrySignInAsync($"{Guid.NewGuid():N}@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);

        var noKey = await new HttpClient { BaseAddress = system.IdentityUrl }
            .PostAsJsonAsync("/internal/tokens", new { userId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
    }

    [Fact]
    public async Task Services_reject_missing_and_forged_tokens()
    {
        var anonymous = new HttpClient { BaseAddress = system.WorkUrl };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/projects")).StatusCode);

        var admin = await AdminAsync();
        var parts = admin.Token.Split('.');
        var forged = new TestUser(admin.Id, admin.Email, $"{parts[0]}.{parts[1]}.AAAA", system);
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.Work.GetAsync("/api/projects")).StatusCode);
    }

    [Fact]
    public async Task Roles_are_enforced_and_projects_are_private()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var ticket = await CreateTicketAsync(admin, project, "Secret");

        var viewer = await InviteToProjectAsync(admin, project, ProjectRole.Viewer);
        var member = await InviteToProjectAsync(admin, project, ProjectRole.Member);
        var outsider = await InviteToProjectAsync(admin, await CreateProjectAsync(admin), ProjectRole.Member);

        Assert.Equal(HttpStatusCode.OK, (await viewer.Work.GetAsync($"/api/tickets/{ticket}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Work.PostAsJsonAsync($"/api/tickets/{ticket}/comments", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Work.PostAsJsonAsync($"/api/tickets/{ticket}/work-logs", new { time = "1h" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await member.Work.PostAsJsonAsync($"/api/tickets/{ticket}/comments", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Work.DeleteAsync($"/api/tickets/{ticket}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.Work.PostAsJsonAsync("/api/projects", new { key = "NOPE", name = "No" })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Work.GetAsync($"/api/tickets/{ticket}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.Work.GetAsync($"/api/projects/{project}/tickets")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_member_ends_access_on_the_next_request()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var member = await InviteToProjectAsync(admin, project, ProjectRole.Member);

        (await admin.Work.DeleteAsync($"/api/projects/{project}/members/{member.Id}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await member.Work.GetAsync($"/api/projects/{project}")).StatusCode);
    }

    [Fact]
    public async Task Deactivated_users_cannot_get_a_token_or_sign_in()
    {
        var admin = await AdminAsync();
        var member = await InviteToProjectAsync(admin, await CreateProjectAsync(admin), ProjectRole.Member);

        (await admin.Identity.PostAsJsonAsync($"/api/users/{member.Id}/active", new { isActive = false })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await system.TrySignInAsync(member.Email)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await system.InternalClient().PostAsJsonAsync("/internal/tokens", new { userId = member.Id })).StatusCode);
    }

    [Fact]
    public async Task Crossing_estimate_thresholds_reaches_the_bell_once_each_and_by_email()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var ticket = await CreateTicketAsync(admin, project, "Estimated", admin.Id, "1h");

        (await admin.Work.PostAsJsonAsync($"/api/tickets/{ticket}/work-logs", new { time = "50m" })).EnsureSuccessStatusCode();
        await SystemFixture.WaitUntilAsync(
            async () => (await BellAsync(admin)).Contains($"{ticket} has used 83% of its estimate"), "the 80% alert");

        (await admin.Work.PostAsJsonAsync($"/api/tickets/{ticket}/work-logs", new { time = "5m" })).EnsureSuccessStatusCode();
        (await admin.Work.PostAsJsonAsync($"/api/tickets/{ticket}/work-logs", new { time = "10m" })).EnsureSuccessStatusCode();
        await SystemFixture.WaitUntilAsync(
            async () => (await BellAsync(admin)).Contains($"{ticket} has exceeded its estimate"), "the exceeded alert");

        var alerts = (await BellAsync(admin)).Where(title => title.StartsWith($"{ticket} has ")).ToList();
        Assert.Equal(2, alerts.Count);
        await SystemFixture.WaitUntilAsync(
            () => Task.FromResult(system.Emails.Sent.Any(email => email.Subject == $"{ticket} has exceeded its estimate")), "the queued email");
    }

    [Fact]
    public async Task Estimate_changes_notify_the_other_people_on_the_ticket()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var member = await InviteToProjectAsync(admin, project, ProjectRole.Member);
        var ticket = await CreateTicketAsync(admin, project, "Change me", member.Id, "2h");

        var update = await admin.Work.PutAsJsonAsync($"/api/tickets/{ticket}", new
        {
            title = "Change me", type = TicketType.Task, priority = TicketPriority.Medium, assigneeId = member.Id, estimate = "4h"
        });
        update.EnsureSuccessStatusCode();

        await SystemFixture.WaitUntilAsync(
            async () => (await BellAsync(member)).Contains($"The estimate for {ticket} changed"), "the estimate notification");
        Assert.Contains($"{ticket} was assigned to you", await BellAsync(member));
    }

    [Fact]
    public async Task Due_reminders_go_once_and_stop_at_done()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var member = await InviteToProjectAsync(admin, project, ProjectRole.Member);

        var soon = await CreateTicketAsync(admin, project, "Soon", member.Id, dueAt: DateTime.UtcNow.AddHours(2));
        await SystemFixture.WaitUntilAsync(async () => (await BellAsync(member)).Contains($"{soon} is due soon"), "the due-soon reminder");
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Single(await BellAsync(member), title => title == $"{soon} is due soon");

        var done = await CreateTicketAsync(admin, project, "Finished in time", dueAt: DateTime.UtcNow.AddHours(-1));
        (await admin.Work.PostAsJsonAsync($"/api/tickets/{done}/move", new { status = TicketStatus.Done })).EnsureSuccessStatusCode();
        var update = await admin.Work.PutAsJsonAsync($"/api/tickets/{done}", new
        {
            title = "Finished in time", type = TicketType.Task, priority = TicketPriority.Medium, assigneeId = member.Id,
            dueAt = DateTime.UtcNow.AddHours(-1)
        });
        update.EnsureSuccessStatusCode();
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain($"{done} is overdue", await BellAsync(member));
    }

    [Fact]
    public async Task Custom_reminders_fire_for_their_owner()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var ticket = await CreateTicketAsync(admin, project, "Remind me");

        await SystemFixture.WaitUntilAsync(async () =>
            (await admin.Notifications.PostAsJsonAsync("/api/reminders", new { ticketKey = ticket, remindAt = DateTime.UtcNow.AddSeconds(2), note = "Check this" })).IsSuccessStatusCode,
            "the ticket to reach Notifications");

        await SystemFixture.WaitUntilAsync(async () => (await BellAsync(admin)).Contains($"Reminder: {ticket}"), "the custom reminder");
    }

    [Fact]
    public async Task Deleted_tickets_cannot_be_changed_until_restored()
    {
        var admin = await AdminAsync();
        var project = await CreateProjectAsync(admin);
        var ticket = await CreateTicketAsync(admin, project, "Delete me");

        (await admin.Work.DeleteAsync($"/api/tickets/{ticket}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Work.PostAsJsonAsync($"/api/tickets/{ticket}/comments", new { body = "x" })).StatusCode);
        Assert.Empty((await admin.Work.GetFromJsonAsync<List<Card>>($"/api/projects/{project}/tickets"))!);

        (await admin.Work.PostAsJsonAsync($"/api/tickets/{ticket}/restore", new { })).EnsureSuccessStatusCode();
        Assert.Single((await admin.Work.GetFromJsonAsync<List<Card>>($"/api/projects/{project}/tickets"))!);
    }

    [Fact]
    public async Task Duplicate_events_are_handled_once()
    {
        var admin = await AdminAsync();
        var title = $"Duplicate {Guid.NewGuid():N}";
        var message = new CommentAdded(Guid.NewGuid(), "DUP-1", title, Guid.NewGuid(), "Someone", [admin.Id]);
        var marker = new CommentAdded(Guid.NewGuid(), "DUP-2", title, Guid.NewGuid(), "Marker", [admin.Id]);

        await using var connection = await new ConnectionFactory { Uri = new Uri(system.RabbitConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var id = Guid.NewGuid();
        await PublishAsync(channel, id, System.Text.Json.JsonSerializer.Serialize(message));
        await PublishAsync(channel, id, System.Text.Json.JsonSerializer.Serialize(message));
        await PublishAsync(channel, Guid.NewGuid(), System.Text.Json.JsonSerializer.Serialize(marker));

        // Delivery is in order, so once the marker lands both copies have been seen.
        await SystemFixture.WaitUntilAsync(async () => (await BellAsync(admin)).Contains("Marker commented on DUP-2"), "the marker event");
        Assert.Single(await BellAsync(admin), item => item == "Someone commented on DUP-1");
    }

    [Fact]
    public async Task Poison_messages_go_to_the_dead_letter_queue_and_do_not_block_others()
    {
        var admin = await AdminAsync();
        await using var connection = await new ConnectionFactory { Uri = new Uri(system.RabbitConnectionString) }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var before = await channel.MessageCountAsync("simpletasks.dead");

        await PublishAsync(channel, Guid.NewGuid(), "this is not json");

        await SystemFixture.WaitUntilAsync(
            async () => await channel.MessageCountAsync("simpletasks.dead") > before, "the poison message to be dead-lettered");

        var after = new CommentAdded(Guid.NewGuid(), "NEXT-1", "After poison", Guid.NewGuid(), "Later", [admin.Id]);
        await PublishAsync(channel, Guid.NewGuid(), System.Text.Json.JsonSerializer.Serialize(after));
        await SystemFixture.WaitUntilAsync(async () => (await BellAsync(admin)).Contains("Later commented on NEXT-1"), "the next event");
    }

    private static Task PublishAsync(IChannel channel, Guid id, string json) =>
        channel.BasicPublishAsync("simpletasks.events", nameof(CommentAdded), false,
            new BasicProperties { MessageId = id.ToString(), Type = nameof(CommentAdded), DeliveryMode = DeliveryModes.Persistent },
            Encoding.UTF8.GetBytes(json)).AsTask();

    [Fact]
    public async Task Original_tasks_are_imported_once_and_the_source_is_unchanged()
    {
        var admin = await AdminAsync();

        var tickets = await admin.Work.GetFromJsonAsync<List<Card>>("/api/projects/ST/tickets");

        Assert.Equal(2, tickets!.Count);
        Assert.Contains(tickets, ticket => ticket is { Key: "ST-1", Title: "Open task", Status: TicketStatus.ToDo });
        Assert.Contains(tickets, ticket => ticket is { Key: "ST-2", Title: "Finished task", Status: TicketStatus.Done });

        await using var connection = new SqlConnection(system.SharedConnection);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM dbo.Tasks";
        Assert.Equal(2, await count.ExecuteScalarAsync());
    }
}
