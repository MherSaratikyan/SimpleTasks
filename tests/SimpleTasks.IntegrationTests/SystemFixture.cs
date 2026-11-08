using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SimpleTasks.Notifications.Data;
using SimpleTasks.Notifications.Features;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;

[assembly: AssemblyFixture(typeof(SimpleTasks.IntegrationTests.SystemFixture))]

namespace SimpleTasks.IntegrationTests;

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentBag<EmailMessage> Sent { get; } = [];

    public Task SendAsync(EmailMessage email, CancellationToken ct)
    {
        Sent.Add(email);
        return Task.CompletedTask;
    }
}

public sealed class ServiceFactory<TEntryPoint>(
    Dictionary<string, string?> settings, Action<IServiceCollection>? services = null) : WebApplicationFactory<TEntryPoint>
    where TEntryPoint : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    public Uri Start()
    {
        UseKestrel(0);
        StartServer();
        var address = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address);
    }
}

/// <summary>The three real services, on real ports, with a real SQL Server and RabbitMQ in throwaway containers.</summary>
public sealed class SystemFixture : IAsyncLifetime
{
    public const string InternalKey = "integration-test-key";
    public const string FirstAdminEmail = "first.admin@example.test";
    public const string LegacyDatabase = "OriginalSimpleTasks";

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "1200").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4.3-management").Build();

    private ServiceFactory<IdentityEntryPoint> _identity = null!;
    private ServiceFactory<WorkEntryPoint> _work = null!;
    private ServiceFactory<NotificationsEntryPoint> _notifications = null!;

    public CapturingEmailSender Emails { get; } = new();
    public Uri IdentityUrl { get; private set; } = null!;
    public Uri WorkUrl { get; private set; } = null!;
    public Uri NotificationsUrl { get; private set; } = null!;
    public string SharedConnection { get; private set; } = null!;
    public string RabbitConnectionString => _rabbit.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _rabbit.StartAsync());
        SharedConnection = new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = LegacyDatabase }.ConnectionString;
        await SeedOriginalTasksAsync();

        var shared = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = SharedConnection,
            ["ConnectionStrings:messaging"] = RabbitConnectionString,
            ["Internal:ApiKey"] = InternalKey,
            ["Identity:FirstAdminEmail"] = FirstAdminEmail,
            ["Reminders:IntervalSeconds"] = "1"
        };

        _identity = new ServiceFactory<IdentityEntryPoint>(shared);
        IdentityUrl = _identity.Start();

        var withIdentity = new Dictionary<string, string?>(shared) { ["services:identity:http:0"] = IdentityUrl.ToString() };
        _work = new ServiceFactory<WorkEntryPoint>(withIdentity);
        WorkUrl = _work.Start();

        _notifications = new ServiceFactory<NotificationsEntryPoint>(withIdentity,
            services => services.AddSingleton<IEmailSender>(Emails));
        NotificationsUrl = _notifications.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _notifications.DisposeAsync();
        await _work.DisposeAsync();
        await _identity.DisposeAsync();
        await _sql.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    private async Task SeedOriginalTasksAsync()
    {
        await using (var master = new SqlConnection(_sql.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE {LegacyDatabase}";
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(SharedConnection);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            CREATE TABLE dbo.Tasks (
                Id int IDENTITY PRIMARY KEY, Title nvarchar(100) NOT NULL,
                IsCompleted bit NOT NULL, CreatedAt datetime2 NOT NULL);
            INSERT dbo.Tasks (Title, IsCompleted, CreatedAt) VALUES
                ('Open task', 0, '2026-01-02T03:04:05'), ('Finished task', 1, '2026-01-03T03:04:05');
            """;
        await seed.ExecuteNonQueryAsync();
    }

    public HttpClient InternalClient()
    {
        var client = new HttpClient { BaseAddress = IdentityUrl };
        client.DefaultRequestHeaders.Add("X-Internal-Key", InternalKey);
        return client;
    }

    public const string TestPassword = "correct horse battery";

    private readonly SemaphoreSlim _adminGate = new(1, 1);
    private TestUser? _admin;

    public Task<HttpResponseMessage> TryLoginAsync(string email, string password) =>
        InternalClient().PostAsJsonAsync("/internal/login", new { email, password });

    public async Task<TestUser> LoginAsync(string email, string password = TestPassword)
    {
        using var response = await TryLoginAsync(email, password);
        response.EnsureSuccessStatusCode();
        var user = (await response.Content.ReadFromJsonAsync<SignedIn>())!;
        return new TestUser(user.UserId, email, await TokenAsync(user.UserId), this);
    }

    /// <summary>Waits for the email Notifications sent to this address and returns the token in its link.</summary>
    public async Task<string> LinkTokenAsync(string to, string subjectStart)
    {
        EmailMessage? email = null;
        await WaitUntilAsync(() =>
        {
            email = Emails.Sent.Where(sent => sent.To == to && sent.Subject.StartsWith(subjectStart, StringComparison.Ordinal))
                .OrderByDescending(sent => sent.NextAttemptAt).FirstOrDefault();
            return Task.FromResult(email is not null);
        }, $"the \"{subjectStart}\" email to {to}");
        var link = email!.LinkPath!;
        return Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);
    }

    public Task<HttpResponseMessage> SetPasswordAsync(string token, string password, string? displayName = null) =>
        InternalClient().PostAsJsonAsync("/internal/password", new { token, password, displayName });

    /// <summary>Follows the invite email: choose a name and password, then log in.</summary>
    public async Task<TestUser> AcceptInviteAsync(string email)
    {
        var token = await LinkTokenAsync(email, "You're invited");
        (await SetPasswordAsync(token, TestPassword, email.Split('@')[0])).EnsureSuccessStatusCode();
        return await LoginAsync(email);
    }

    /// <summary>The first admin gets in the way a real one would: through the setup link emailed at startup.</summary>
    public async Task<TestUser> AdminAsync()
    {
        await _adminGate.WaitAsync();
        try
        {
            if (_admin is null)
            {
                var token = await LinkTokenAsync(FirstAdminEmail, "Set your Simple Tasks password");
                (await SetPasswordAsync(token, TestPassword)).EnsureSuccessStatusCode();
                _admin = await LoginAsync(FirstAdminEmail);
            }
            // A fresh service token each time, so long test runs never use an expired one.
            return _admin with { Token = await TokenAsync(_admin.Id) };
        }
        finally
        {
            _adminGate.Release();
        }
    }

    public async Task<string> TokenAsync(Guid userId)
    {
        using var response = await InternalClient().PostAsJsonAsync("/internal/tokens", new { userId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Issued>())!.Token;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string waitingFor, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(200);
        }
        Assert.Fail($"Timed out waiting for {waitingFor}.");
    }

    private sealed record SignedIn(Guid UserId);
    private sealed record Issued(string Token);
}

public sealed record TestUser(Guid Id, string Email, string Token, SystemFixture System)
{
    public HttpClient Identity => Client(System.IdentityUrl);
    public HttpClient Work => Client(System.WorkUrl);
    public HttpClient Notifications => Client(System.NotificationsUrl);

    private HttpClient Client(Uri baseAddress)
    {
        var client = new HttpClient { BaseAddress = baseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }
}
