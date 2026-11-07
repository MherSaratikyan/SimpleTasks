using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SimpleTasks.Contracts;
using SimpleTasks.Messaging;

namespace SimpleTasks.Identity.Data;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? GoogleSubject { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsSiteAdmin { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public ProjectRole? Role { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
}

public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : DbContext(options), IMessagingDbContext
{
    public const string DatabaseName = "SimpleTasks_Identity";

    public DbSet<User> Users => Set<User>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.ApplyMessaging();
        model.Entity<User>(entity =>
        {
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.Property(user => user.DisplayName).HasMaxLength(200);
            entity.Property(user => user.AvatarUrl).HasMaxLength(500);
            entity.Property(user => user.GoogleSubject).HasMaxLength(100);
            entity.Property(user => user.TimeZoneId).HasMaxLength(100);
            entity.HasIndex(user => user.Email).IsUnique();
        });
        model.Entity<Invite>(entity =>
        {
            entity.Property(invite => invite.Email).HasMaxLength(320);
            entity.Property(invite => invite.ProjectName).HasMaxLength(100);
            entity.HasIndex(invite => invite.Email);
        });
    }
}

public sealed class IdentityDbFactory : IDesignTimeDbContextFactory<IdentityDb>
{
    public IdentityDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<IdentityDb>().UseSqlServer("Server=design-time-only").Options);
}
