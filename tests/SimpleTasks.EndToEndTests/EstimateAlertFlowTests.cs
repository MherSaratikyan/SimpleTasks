using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Xunit;

namespace SimpleTasks.EndToEndTests;

/// <summary>Drives the web app as a browser would, with every service running under Aspire.</summary>
public class EstimateAlertFlowTests
{
    private const string AdminEmail = "e2e.admin@simpletasks.test";

    [Fact]
    public async Task Crossing_80_percent_of_an_estimate_reaches_the_bell()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SimpleTasks_AppHost>(
            ["UseContainerSql=true", "PublicUrl=http://localhost:5080", $"FirstAdminEmail={AdminEmail}"], ct);
        await using var app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("web", ct);

        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var browser = new HttpClient(handler) { BaseAddress = app.GetEndpoint("web") };

        await PostFormAsync(browser, "/Account/Login", "/Account/Login?handler=Dev",
            new() { ["email"] = AdminEmail, ["returnUrl"] = "/" }, ct);
        await PostFormAsync(browser, "/projects", "/projects",
            new() { ["name"] = "End to end", ["key"] = "E2E" }, ct);

        var newTicket = await browser.GetStringAsync("/projects/E2E/tickets/new", ct);
        var assignee = Regex.Match(newTicket, "name=\"Form.AssigneeId\">.*?<option value=\"([0-9a-f-]{36})\"", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(assignee);
        await PostFormAsync(browser, "/projects/E2E/tickets/new", "/projects/E2E/tickets/new", new()
        {
            ["Form.Title"] = "Reach the bell",
            ["Form.Type"] = "Task",
            ["Form.Priority"] = "Medium",
            ["Form.AssigneeId"] = assignee,
            ["Form.Estimate"] = "1h"
        }, ct);

        Assert.Contains("E2E-1", await browser.GetStringAsync("/projects/E2E/board", ct));

        await PostFormAsync(browser, "/tickets/E2E-1", "/tickets/E2E-1?handler=LogWork", new() { ["time"] = "50m" }, ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var bell = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            bell = await browser.GetStringAsync("/notifications/bell", ct);
            if (bell.Contains("E2E-1 has used 83% of its estimate"))
            {
                return;
            }
            await Task.Delay(500, ct);
        }
        Assert.Fail($"The alert never reached the bell. Last response: {bell}");
    }

    private static async Task PostFormAsync(
        HttpClient browser, string formPage, string action, Dictionary<string, string> fields, CancellationToken ct)
    {
        var page = await browser.GetStringAsync(formPage, ct);
        fields["__RequestVerificationToken"] =
            Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var response = await browser.PostAsync(action, new FormUrlEncodedContent(fields), ct);
        response.EnsureSuccessStatusCode();
    }
}
