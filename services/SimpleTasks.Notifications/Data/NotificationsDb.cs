using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;

namespace SimpleTasks.Notifications.Data;

public sealed class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public sealed class EmailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? LinkPath { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? GaveUpAt { get; set; }
}

/// <summary>A row exists only when a user has turned email off for a type.</summary>
public sealed class EmailOptOut
{
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
}

public sealed class UserCopy
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
}

public sealed class TicketCopy
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Guid? AssigneeId { get; set; }
    public DateTime? DueAt { get; set; }
    public bool IsDone { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? DueSoonSentFor { get; set; }
    public DateTime? LastOverdueSentAt { get; set; }
}

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TicketId { get; set; }
    public string? Note { get; set; }
    public DateTime RemindAt { get; set; }
    public DateTime? FiredAt { get; set; }
}

public sealed class NotificationsDb(DbContextOptions<NotificationsDb> options) : DbContext(options), IMessagingDbContext
{
    public const string DatabaseName = "SimpleTasks_Notifications";

    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<EmailMessage> Emails => Set<EmailMessage>();
    public DbSet<EmailOptOut> EmailOptOuts => Set<EmailOptOut>();
    public DbSet<UserCopy> Users => Set<UserCopy>();
    public DbSet<TicketCopy> Tickets => Set<TicketCopy>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyMessaging();
        model.Entity<Notification>(entity =>
        {
            entity.Property(notification => notification.Title).HasMaxLength(300);
            entity.Property(notification => notification.Body).HasMaxLength(1000);
            entity.Property(notification => notification.Url).HasMaxLength(300);
            entity.HasIndex(notification => new { notification.UserId, notification.CreatedAt });
        });
        model.Entity<EmailMessage>(entity =>
        {
            entity.Property(email => email.To).HasMaxLength(320);
            entity.Property(email => email.Subject).HasMaxLength(300);
            entity.Property(email => email.LinkPath).HasMaxLength(300);
            entity.HasIndex(email => new { email.SentAt, email.GaveUpAt, email.NextAttemptAt });
        });
        model.Entity<EmailOptOut>().HasKey(optOut => new { optOut.UserId, optOut.Type });
        model.Entity<UserCopy>(entity =>
        {
            entity.Property(user => user.Id).ValueGeneratedNever();
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.Property(user => user.DisplayName).HasMaxLength(200);
            entity.Property(user => user.TimeZoneId).HasMaxLength(100);
        });
        model.Entity<TicketCopy>(entity =>
        {
            entity.Property(ticket => ticket.Id).ValueGeneratedNever();
            entity.Property(ticket => ticket.Key).HasMaxLength(30);
            entity.Property(ticket => ticket.Title).HasMaxLength(255);
            entity.HasIndex(ticket => ticket.Key);
            entity.HasIndex(ticket => ticket.DueAt);
        });
        model.Entity<Reminder>(entity =>
        {
            entity.Property(reminder => reminder.Note).HasMaxLength(500);
            entity.HasIndex(reminder => new { reminder.FiredAt, reminder.RemindAt });
        });
    }
}

public sealed class NotificationsDbFactory : IDesignTimeDbContextFactory<NotificationsDb>
{
    public NotificationsDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NotificationsDb>().UseSqlServer("Server=design-time-only").Options);
}
