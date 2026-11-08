using System.Net;
using System.Net.Http.Json;
using SimpleTasks.Contracts;
using Xunit;

namespace SimpleTasks.IntegrationTests;

public class PasswordTests(SystemFixture system)
{
    private async Task<string> InviteAsync()
    {
        var admin = await system.AdminAsync();
        var email = $"{Guid.NewGuid():N}@example.test";
        (await admin.Identity.PostAsJsonAsync("/api/invites", new { email })).EnsureSuccessStatusCode();
        return email;
    }

    [Fact]
    public async Task An_invite_link_creates_the_account_once()
    {
        var email = await InviteAsync();
        var token = await system.LinkTokenAsync(email, "You're invited");

        var info = await system.InternalClient().GetFromJsonAsync<Dictionary<string, string>>(
            $"/internal/password-token?token={Uri.EscapeDataString(token)}");
        Assert.Equal("invite", info!["kind"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await system.SetPasswordAsync(token, "short", "Newcomer")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await system.SetPasswordAsync(token, SystemFixture.TestPassword, null)).StatusCode);
        (await system.SetPasswordAsync(token, SystemFixture.TestPassword, "Newcomer")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await system.SetPasswordAsync(token, "another good password", "Again")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await system.TryLoginAsync(email, SystemFixture.TestPassword)).StatusCode);
    }

    [Fact]
    public async Task Inviting_someone_who_already_has_an_account_is_refused()
    {
        var admin = await system.AdminAsync();
        var response = await admin.Identity.PostAsJsonAsync("/api/invites", new { email = SystemFixture.FirstAdminEmail });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var email = await InviteAsync();
        await system.AcceptInviteAsync(email);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await system.TryLoginAsync(email, "wrong password!")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Locked, (await system.TryLoginAsync(email, SystemFixture.TestPassword)).StatusCode);
    }

    [Fact]
    public async Task A_reset_link_replaces_the_password_and_unlocks_the_account()
    {
        var email = await InviteAsync();
        await system.AcceptInviteAsync(email);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await system.TryLoginAsync(email, "wrong password!");
        }

        Assert.Equal(HttpStatusCode.Accepted,
            (await system.InternalClient().PostAsJsonAsync("/internal/password-reset", new { email })).StatusCode);
        var token = await system.LinkTokenAsync(email, "Set your Simple Tasks password");
        (await system.SetPasswordAsync(token, "a brand new password")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await system.TryLoginAsync(email, SystemFixture.TestPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await system.TryLoginAsync(email, "a brand new password")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await system.SetPasswordAsync(token, "yet another password")).StatusCode);
    }

    [Fact]
    public async Task Reset_requests_reveal_nothing_about_unknown_emails()
    {
        var unknown = $"{Guid.NewGuid():N}@example.test";
        var response = await system.InternalClient().PostAsJsonAsync("/internal/password-reset", new { email = unknown });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Wait for a later email to go out, so an email for the unknown address would have too.
        var marker = await InviteAsync();
        await system.LinkTokenAsync(marker, "You're invited");
        Assert.DoesNotContain(system.Emails.Sent, email => email.To == unknown);
    }

    [Fact]
    public async Task Garbage_tokens_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await system.SetPasswordAsync("not-a-token", SystemFixture.TestPassword, "X")).StatusCode);
        var info = await system.InternalClient().GetAsync("/internal/password-token?token=not-a-token");
        Assert.Equal(HttpStatusCode.NotFound, info.StatusCode);
    }

    [Fact]
    public async Task Accepting_an_invite_grants_the_project_role_it_named()
    {
        var admin = await system.AdminAsync();
        var key = $"R{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        (await admin.Work.PostAsJsonAsync("/api/projects", new { key, name = "Roles" })).EnsureSuccessStatusCode();
        var email = $"{Guid.NewGuid():N}@example.test";
        (await admin.Work.PostAsJsonAsync($"/api/projects/{key}/invites", new { email, role = ProjectRole.Viewer })).EnsureSuccessStatusCode();

        var viewer = await system.AcceptInviteAsync(email);

        await SystemFixture.WaitUntilAsync(async () =>
            (await viewer.Work.GetAsync($"/api/projects/{key}")).IsSuccessStatusCode, "the membership to reach Work");
        var project = await viewer.Work.GetFromJsonAsync<Dictionary<string, object>>($"/api/projects/{key}");
        Assert.Equal((int)ProjectRole.Viewer, ((System.Text.Json.JsonElement)project!["role"]).GetInt32());
    }
}
