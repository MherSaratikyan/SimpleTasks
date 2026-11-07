using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace SimpleTasks.Messaging;

/// <summary>Publishes saved outbox rows in order. A crash can repeat a message but cannot lose one.</summary>
internal sealed class OutboxPublisher<TDb>(
    IServiceScopeFactory scopes,
    RabbitConnection rabbit,
    ILogger<OutboxPublisher<TDb>> logger) : BackgroundService
    where TDb : DbContext, IMessagingDbContext
{
    private const int BatchSize = 50;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                channel ??= await OpenChannelAsync(stoppingToken);
                var published = await PublishBatchAsync(channel, stoppingToken);
                if (published < BatchSize)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Outbox publish failed; retrying.");
                if (channel is not null)
                {
                    await channel.DisposeAsync();
                    channel = null;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await Topology.DeclareAsync(channel, ct);
        return channel;
    }

    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();

        var batch = await db.OutboxMessages
            .Where(message => message.PublishedAt == null)
            .OrderBy(message => message.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            var properties = new BasicProperties
            {
                MessageId = message.MessageId.ToString(),
                Type = message.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent
            };
            await channel.BasicPublishAsync(
                Topology.Exchange, message.Type, mandatory: false, properties, Encoding.UTF8.GetBytes(message.Payload), ct);
            message.PublishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        if (batch.Count > 0)
        {
            var cutoff = DateTime.UtcNow - Retention;
            await db.OutboxMessages.Where(message => message.PublishedAt < cutoff).ExecuteDeleteAsync(ct);
        }
        return batch.Count;
    }
}
