using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using SimpleTasks.Contracts;

namespace SimpleTasks.Services;

public sealed record PasswordLink(string Kind, string Email);
public sealed record SignInResponse(Guid UserId, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsSiteAdmin);
public sealed record DevUser(string Email, string DisplayName, bool IsSiteAdmin);
public sealed record UserView(Guid Id, string Email, string DisplayName, string? AvatarUrl, string TimeZoneId, bool IsSiteAdmin, bool IsActive);
public sealed record InviteView(Guid Id, string Email, string? ProjectName, DateTime ExpiresAt, DateTime? AcceptedAt);

public sealed record ProjectView(Guid Id, string Key, string Name, string? Description, ProjectRole Role);
public sealed record MemberView(Guid UserId, string DisplayName, string Email, string? AvatarUrl, ProjectRole Role);
public sealed record LabelView(Guid Id, string Name, string Color);
public sealed record UserRef(Guid Id, string DisplayName, string? AvatarUrl);
public sealed record TicketCard(
    string Key, string Title, TicketType Type, TicketStatus Status, TicketPriority Priority, UserRef? Assignee,
    int? EstimateMinutes, int LoggedMinutes, string? Estimate, string Logged, DateTime? DueAt, double Rank,
    IReadOnlyList<LabelView> Labels, DateTime? DeletedAt);
public sealed record CommentView(Guid Id, UserRef? Author, string Body, DateTime CreatedAt);
public sealed record WorkLogView(Guid Id, UserRef? User, string Time, string? Note, DateTime CreatedAt);
public sealed record ActivityView(UserRef? Actor, string Text, DateTime CreatedAt);
public sealed record TicketDetail(
    TicketCard Card, string? Description, UserRef? Reporter, DateTime CreatedAt, DateTime UpdatedAt, ProjectRole Role,
    string ProjectKey, string ProjectName,
    IReadOnlyList<CommentView> Comments, IReadOnlyList<WorkLogView> WorkLogs, IReadOnlyList<ActivityView> Activity);
public sealed record SaveTicket(
    string Title, string? Description, TicketType Type, TicketPriority Priority, Guid? AssigneeId,
    string? Estimate, DateTime? DueAt, IReadOnlyList<Guid>? LabelIds);

public sealed record NotificationView(Guid Id, NotificationType Type, string Title, string Body, string Url, DateTime CreatedAt, bool IsRead);
public sealed record NotificationFeed(int Unread, IReadOnlyList<NotificationView> Items);
public sealed record PreferenceView(NotificationType Type, bool Email);
public sealed record ReminderView(Guid Id, string TicketKey, string? Note, DateTime RemindAt);

/// <summary>A service said no. Message is safe to show to the user.</summary>
public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>The user was deactivated or no longer exists, so they must be signed out.</summary>
public sealed class AccountRevokedException : Exception;

/// <summary>Calls Identity with the internal key, for steps before a user has a token.</summary>
public sealed class IdentityInternalClient(HttpClient http)
{
    /// <summary>Returns the user, or a message to show when the email and password aren't accepted.</summary>
    public Task<(SignInResponse? User, string? Error)> LoginAsync(string email, string password, CancellationToken ct) =>
        SendForUserAsync("/internal/login", new { email, password }, ct);

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/internal/password-reset", new { email }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Says whether a link is for a new account ("invite") or a reset ("reset"), or null if it no longer works.</summary>
    public async Task<PasswordLink?> CheckPasswordLinkAsync(string token, CancellationToken ct)
    {
        using var response = await http.GetAsync($"/internal/password-token?token={Uri.EscapeDataString(token)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PasswordLink>(ct);
    }

    public Task<(SignInResponse? User, string? Error)> SetPasswordAsync(
        string token, string password, string? displayName, CancellationToken ct) =>
        SendForUserAsync("/internal/password", new { token, password, displayName }, ct);

    public async Task<SignInResponse?> DevSignInAsync(string email, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/internal/dev-sign-in", new { email }, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignInResponse>(ct);
    }

    private async Task<(SignInResponse? User, string? Error)> SendForUserAsync(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        if ((int)response.StatusCode >= 500)
        {
            response.EnsureSuccessStatusCode();
        }
        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<SignInResponse>(ct), null);
        }
        var problem = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
        return (null, problem?.Error ?? "That didn't work. Please try again.");
    }

    private sealed record ErrorBody(string? Error);

    public async Task<List<DevUser>> GetDevUsersAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<List<DevUser>>("/internal/dev-users", ct) ?? [];

    public async Task<(string Token, DateTime ExpiresAt)> GetTokenAsync(Guid userId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/internal/tokens", new { userId }, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AccountRevokedException();
        }
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return (body!.Token, body.ExpiresAt);
    }

    private sealed record TokenResponse(string Token, DateTime ExpiresAt);
}

/// <summary>Keeps one short-lived service token per user.</summary>
public sealed class ServiceTokens(IdentityInternalClient identity, IMemoryCache cache)
{
    public async Task<string> GetAsync(Guid userId, CancellationToken ct)
    {
        if (cache.TryGetValue(userId, out string? token))
        {
            return token!;
        }
        var (fresh, expiresAt) = await identity.GetTokenAsync(userId, ct);
        cache.Set(userId, fresh, expiresAt.AddMinutes(-1) - DateTime.UtcNow);
        return fresh;
    }
}

public sealed class ServiceTokenHandler(ServiceTokens tokens, IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var id = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(Guid.Parse(id), ct));
        }
        return await base.SendAsync(request, ct);
    }
}

public abstract class ServiceClient(HttpClient http)
{
    protected async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    protected async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    protected async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if ((int)response.StatusCode >= 500)
        {
            response.EnsureSuccessStatusCode();
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You don't have permission to do that.",
            HttpStatusCode.NotFound => "That couldn't be found.",
            _ => "That request couldn't be completed."
        };
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            message = problem?.Error ?? message;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }
        throw new ApiException(response.StatusCode, message);
    }

    private sealed record ErrorBody(string? Error);
}

public sealed class IdentityClient(HttpClient http) : ServiceClient(http)
{
    public Task<List<UserView>> UsersAsync(CancellationToken ct) => GetAsync<List<UserView>>("/api/users", ct);
    public Task<List<InviteView>> InvitesAsync(CancellationToken ct) => GetAsync<List<InviteView>>("/api/invites", ct);
    public Task InviteAsync(string email, CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/invites", new { email }, ct);
    public Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct) => SendAsync(HttpMethod.Post, $"/api/users/{id}/active", new { isActive }, ct);
    public Task SetTimeZoneAsync(string timeZoneId, CancellationToken ct) => SendAsync(HttpMethod.Put, "/api/me", new { timeZoneId }, ct);
}

public sealed record TicketFilter(string? Q, Guid? Assignee, Guid? Label, TicketType? Type, TicketPriority? Priority)
{
    public string ToQuery()
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(Q)) query.Add($"q={Uri.EscapeDataString(Q)}");
        if (Assignee is not null) query.Add($"assignee={Assignee}");
        if (Label is not null) query.Add($"label={Label}");
        if (Type is not null) query.Add($"type={Type}");
        if (Priority is not null) query.Add($"priority={Priority}");
        return query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
    }
}

public sealed class WorkClient(HttpClient http) : ServiceClient(http)
{
    public Task<List<ProjectView>> ProjectsAsync(CancellationToken ct) => GetAsync<List<ProjectView>>("/api/projects", ct);
    public Task<ProjectView> ProjectAsync(string key, CancellationToken ct) => GetAsync<ProjectView>($"/api/projects/{key}", ct);
    public Task CreateProjectAsync(string key, string name, string? description, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/projects", new { key, name, description }, ct);
    public Task<List<MemberView>> MembersAsync(string key, CancellationToken ct) => GetAsync<List<MemberView>>($"/api/projects/{key}/members", ct);
    public Task InviteMemberAsync(string key, string email, ProjectRole role, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/projects/{key}/invites", new { email, role }, ct);
    public Task SetRoleAsync(string key, Guid userId, ProjectRole role, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/api/projects/{key}/members/{userId}", new { role }, ct);
    public Task RemoveMemberAsync(string key, Guid userId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"/api/projects/{key}/members/{userId}", null, ct);
    public Task<List<LabelView>> LabelsAsync(string key, CancellationToken ct) => GetAsync<List<LabelView>>($"/api/projects/{key}/labels", ct);
    public Task CreateLabelAsync(string key, string name, string color, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/projects/{key}/labels", new { name, color }, ct);
    public Task DeleteLabelAsync(string key, Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"/api/projects/{key}/labels/{id}", null, ct);

    public Task<List<TicketCard>> TicketsAsync(string key, TicketFilter filter, CancellationToken ct) =>
        GetAsync<List<TicketCard>>($"/api/projects/{key}/tickets{filter.ToQuery()}", ct);
    public Task<List<TicketCard>> DeletedTicketsAsync(string key, CancellationToken ct) =>
        GetAsync<List<TicketCard>>($"/api/projects/{key}/tickets?deleted=true", ct);
    public Task<List<TicketCard>> MyTicketsAsync(CancellationToken ct) => GetAsync<List<TicketCard>>("/api/tickets/mine", ct);
    public Task<TicketDetail> TicketAsync(string key, CancellationToken ct) => GetAsync<TicketDetail>($"/api/tickets/{key}", ct);
    public async Task<string> CreateTicketAsync(string projectKey, SaveTicket ticket, CancellationToken ct) =>
        (await PostAsync<CreatedTicket>($"/api/projects/{projectKey}/tickets", ticket, ct)).Key;
    public Task UpdateTicketAsync(string key, SaveTicket ticket, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"/api/tickets/{key}", ticket, ct);
    public Task MoveTicketAsync(string key, TicketStatus status, string? afterKey, string? beforeKey, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/tickets/{key}/move", new { status, afterKey, beforeKey }, ct);
    public Task CommentAsync(string key, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/tickets/{key}/comments", new { body }, ct);
    public Task LogWorkAsync(string key, string time, string? note, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/api/tickets/{key}/work-logs", new { time, note }, ct);
    public Task DeleteTicketAsync(string key, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"/api/tickets/{key}", null, ct);
    public Task RestoreTicketAsync(string key, CancellationToken ct) => SendAsync(HttpMethod.Post, $"/api/tickets/{key}/restore", new { }, ct);

    private sealed record CreatedTicket(string Key);
}

public sealed class NotificationsClient(HttpClient http) : ServiceClient(http)
{
    public Task<NotificationFeed> FeedAsync(CancellationToken ct) => GetAsync<NotificationFeed>("/api/notifications", ct);
    public Task MarkReadAsync(Guid id, CancellationToken ct) => SendAsync(HttpMethod.Post, $"/api/notifications/{id}/read", new { }, ct);
    public Task MarkAllReadAsync(CancellationToken ct) => SendAsync(HttpMethod.Post, "/api/notifications/read-all", new { }, ct);
    public Task<List<PreferenceView>> PreferencesAsync(CancellationToken ct) => GetAsync<List<PreferenceView>>("/api/preferences", ct);
    public Task SavePreferencesAsync(IReadOnlyList<PreferenceView> preferences, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, "/api/preferences", preferences, ct);
    public Task<List<ReminderView>> RemindersAsync(string ticketKey, CancellationToken ct) =>
        GetAsync<List<ReminderView>>($"/api/reminders?ticketKey={Uri.EscapeDataString(ticketKey)}", ct);
    public Task AddReminderAsync(string ticketKey, DateTime remindAt, string? note, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/reminders", new { ticketKey, remindAt, note }, ct);
    public Task DeleteReminderAsync(Guid id, CancellationToken ct) => SendAsync(HttpMethod.Delete, $"/api/reminders/{id}", null, ct);
}
