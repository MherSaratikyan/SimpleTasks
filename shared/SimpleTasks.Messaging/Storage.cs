using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimpleTasks.Contracts;

namespace SimpleTasks.Messaging;

public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public DateTime ProcessedAt { get; set; }
}

/// <summary>Implemented by each service's DbContext so events save in the same transaction as its data.</summary>
public interface IMessagingDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<InboxMessage> InboxMessages { get; }
}

public static class MessagingDbExtensions
{
    public static void Publish<TEvent>(this IMessagingDbContext db, TEvent message)
        where TEvent : IIntegrationEvent
    {
        db.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = Guid.NewGuid(),
            Type = typeof(TEvent).Name,
            Payload = JsonSerializer.Serialize(message),
            CreatedAt = DateTime.UtcNow
        });
    }

    public static void ApplyMessaging(this ModelBuilder model)
    {
        model.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.Property(message => message.Type).HasMaxLength(100);
            entity.HasIndex(message => message.PublishedAt);
        });
        model.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages");
            entity.HasKey(message => message.MessageId);
        });
    }
}
