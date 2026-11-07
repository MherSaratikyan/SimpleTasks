using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;

namespace SimpleTasks.Work.Data;


public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LastTicketNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public ProjectRole Role { get; set; }
}

public sealed class Label
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#0052CC";
    public List<Ticket> Tickets { get; set; } = [];
}

public sealed class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TicketType Type { get; set; }
    public TicketStatus Status { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public Guid? AssigneeId { get; set; }
    public Guid? ReporterId { get; set; }
    public int? EstimateMinutes { get; set; }
    public int LoggedMinutes { get; set; }
    public EstimateAlertLevel EstimateAlertLevel { get; set; }
    public DateTime? DueAt { get; set; }
    public double Rank { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    /// <summary>Goes up on every change: guards concurrent edits and orders snapshot events.</summary>
    public long Version { get; set; }

    public List<Label> Labels { get; set; } = [];
    public List<Comment> Comments { get; set; } = [];
    public List<WorkLog> WorkLogs { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];

    public string Key => Domain.TicketKey.Format(Project.Key, Number);
}

public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class WorkLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public int Minutes { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ActivityEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public Guid? ActorId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Names and avatars copied from Identity's events for showing on tickets.</summary>
public sealed class UserCopy
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class LegacyImport
{
    public int Id { get; set; }
    public string SourceDatabase { get; set; } = string.Empty;
    public int TaskCount { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public sealed class WorkDb(DbContextOptions<WorkDb> options) : DbContext(options), IMessagingDbContext
{
    public const string DatabaseName = "SimpleTasks_Work";

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<WorkLog> WorkLogs => Set<WorkLog>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();
    public DbSet<UserCopy> Users => Set<UserCopy>();
    public DbSet<LegacyImport> LegacyImports => Set<LegacyImport>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyMessaging();
        model.Entity<Project>(entity =>
        {
            entity.Property(project => project.Key).HasMaxLength(10);
            entity.Property(project => project.Name).HasMaxLength(100);
            entity.Property(project => project.Description).HasMaxLength(1000);
            entity.HasIndex(project => project.Key).IsUnique();
        });
        model.Entity<ProjectMember>(entity =>
        {
            entity.HasKey(member => new { member.ProjectId, member.UserId });
            entity.HasIndex(member => member.UserId);
            entity.HasOne<Project>().WithMany().HasForeignKey(member => member.ProjectId);
        });
        model.Entity<Label>(entity =>
        {
            entity.Property(label => label.Name).HasMaxLength(50);
            entity.Property(label => label.Color).HasMaxLength(7);
            entity.HasIndex(label => new { label.ProjectId, label.Name }).IsUnique();
            entity.HasOne<Project>().WithMany().HasForeignKey(label => label.ProjectId);
            entity.HasMany(label => label.Tickets).WithMany(ticket => ticket.Labels)
                .UsingEntity(join => join.ToTable("TicketLabels"));
        });
        model.Entity<Ticket>(entity =>
        {
            entity.Property(ticket => ticket.Title).HasMaxLength(255);
            entity.Property(ticket => ticket.Version).IsConcurrencyToken();
            entity.Ignore(ticket => ticket.Key);
            entity.HasIndex(ticket => new { ticket.ProjectId, ticket.Number }).IsUnique();
            entity.HasIndex(ticket => ticket.AssigneeId);
            entity.HasOne(ticket => ticket.Project).WithMany().HasForeignKey(ticket => ticket.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<Comment>().HasOne<Ticket>().WithMany(ticket => ticket.Comments).HasForeignKey(comment => comment.TicketId);
        model.Entity<Comment>().Property(comment => comment.Body).HasMaxLength(10000);
        model.Entity<WorkLog>().HasOne<Ticket>().WithMany(ticket => ticket.WorkLogs).HasForeignKey(log => log.TicketId);
        model.Entity<WorkLog>().Property(log => log.Note).HasMaxLength(500);
        model.Entity<ActivityEntry>().HasOne<Ticket>().WithMany(ticket => ticket.Activity).HasForeignKey(entry => entry.TicketId);
        model.Entity<ActivityEntry>().Property(entry => entry.Text).HasMaxLength(500);
        model.Entity<UserCopy>(entity =>
        {
            entity.ToTable("Users");
            entity.Property(user => user.Id).ValueGeneratedNever();
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.Property(user => user.DisplayName).HasMaxLength(200);
            entity.Property(user => user.AvatarUrl).HasMaxLength(500);
        });
        model.Entity<LegacyImport>().Property(import => import.SourceDatabase).HasMaxLength(128);
    }
}

public sealed class WorkDbFactory : IDesignTimeDbContextFactory<WorkDb>
{
    public WorkDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WorkDb>().UseSqlServer("Server=design-time-only").Options);
}
