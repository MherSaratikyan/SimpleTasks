using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;
using SimpleTasks.Notifications.Data;
using SimpleTasks.ServiceDefaults;

namespace SimpleTasks.Notifications.Features;

[Authorize]
public sealed class NotificationHub : Hub;

public sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}

/// <summary>Records a notification and queues its email in the caller's transaction, then pushes after commit.</summary>
public sealed class NotificationWriter(NotificationsDb db, IHubContext<NotificationHub> hub) : IAfterCommit
{
    private readonly HashSet<Guid> _pushTo = [];

    public async Task NotifyAsync(Guid userId, NotificationType type, string title, string body, string ticketKey, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(user => user.Id == userId, ct);
        if (user is { IsActive: false })
        {
            return;
        }

        var url = $"/tickets/{ticketKey}";
        db.Notifications.Add(new Notification { UserId = userId, Type = type, Title = title, Body = body, Url = url });
        _pushTo.Add(userId);

        if (user is not null && !await db.EmailOptOuts.AnyAsync(optOut => optOut.UserId == userId && optOut.Type == type, ct))
        {
            db.Emails.Add(new EmailMessage { To = user.Email, Subject = title, Body = body, LinkPath = url });
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var userId in _pushTo)
        {
            await hub.Clients.User(userId.ToString()).SendAsync("notify", ct);
        }
        _pushTo.Clear();
    }
}

public interface IEmailSender
{
    Task SendAsync(EmailMessage email, CancellationToken ct);
}

public sealed class LogEmailSender(ILogger<LogEmailSender> logger, IConfiguration configuration) : IEmailSender
{
    public Task SendAsync(EmailMessage email, CancellationToken ct)
    {
        var baseUrl = (configuration["App:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
        logger.LogInformation("Email (not sent in Development) to {To}: {Subject} - {Body} {Link}",
            email.To, email.Subject, email.Body, email.LinkPath is null ? string.Empty : $"{baseUrl}{email.LinkPath}");
        return Task.CompletedTask;
    }
}

public sealed class GmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendAsync(EmailMessage email, CancellationToken ct)
    {
        var address = configuration["Gmail:Address"]
            ?? throw new InvalidOperationException("Gmail:Address is not configured.");
        // Google shows App Passwords in groups of four; the spaces aren't part of the password.
        var appPassword = configuration["Gmail:AppPassword"]?.Replace(" ", string.Empty)
            ?? throw new InvalidOperationException("Gmail:AppPassword is not configured.");
        var baseUrl = (configuration["App:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');

        var body = new BodyBuilder
        {
            TextBody = email.LinkPath is null ? email.Body : $"{email.Body}\n\n{baseUrl}{email.LinkPath}",
            HtmlBody = $"<p>{WebUtility.HtmlEncode(email.Body)}</p>"
                + (email.LinkPath is null ? string.Empty : $"<p><a href=\"{WebUtility.HtmlEncode(baseUrl + email.LinkPath)}\">Open in Simple Tasks</a></p>")
        };
        var message = new MimeMessage { Subject = email.Subject, Body = body.ToMessageBody() };
        message.From.Add(new MailboxAddress("Simple Tasks", address));
        message.To.Add(MailboxAddress.Parse(email.To));

        using var client = new SmtpClient();
        await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(address, appPassword, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Sends queued emails, so a mail outage never affects saving a ticket.</summary>
public sealed class EmailWorker(IServiceScopeFactory scopes, IEmailSender sender, ILogger<EmailWorker> logger) : BackgroundService
{
    public const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await SendDueAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Email pass failed; trying again shortly.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SendDueAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDb>();
        var now = DateTime.UtcNow;
        var due = await db.Emails
            .Where(email => email.SentAt == null && email.GaveUpAt == null && email.NextAttemptAt <= now)
            .OrderBy(email => email.NextAttemptAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var email in due)
        {
            try
            {
                await sender.SendAsync(email, ct);
                email.SentAt = DateTime.UtcNow;
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                email.Attempts++;
                email.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, email.Attempts));
                if (email.Attempts >= MaxAttempts)
                {
                    email.GaveUpAt = DateTime.UtcNow;
                }
                logger.LogWarning(exception, "Could not send email {EmailId} (attempt {Attempt}).", email.Id, email.Attempts);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
