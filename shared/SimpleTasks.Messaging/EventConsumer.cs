using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SimpleTasks.Contracts;

namespace SimpleTasks.Messaging;

public interface IEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    /// <summary>Change the DbContext only; the consumer saves it together with the inbox row.</summary>
    Task HandleAsync(TEvent message, CancellationToken ct);
}

/// <summary>Scoped work to run once a handler's changes are committed, such as a live push.</summary>
public interface IAfterCommit
{
    Task RunAsync(CancellationToken ct);
}

/// <summary>Decides what happens to a failed message, so one bad message can never block the queue behind it.</summary>
public static class RetryPolicy
{
    public static bool ShouldRetry(Exception exception, int attempts) =>
        exception is not (JsonException or FormatException or ArgumentNullException) && attempts < Topology.DeliveryLimit;
}

internal sealed record HandlerRegistration(string RoutingKey, Func<IServiceProvider, string, CancellationToken, Task> Invoke);

internal sealed class EventConsumer<TDb>(
    string queue,
    IEnumerable<HandlerRegistration> registrations,
    IServiceScopeFactory scopes,
    RabbitConnection rabbit,
    ILogger<EventConsumer<TDb>> logger) : BackgroundService
    where TDb : DbContext, IMessagingDbContext
{
    private readonly Dictionary<string, HandlerRegistration> _handlers =
        registrations.ToDictionary(registration => registration.RoutingKey);

    // Prefetch is 1, so only one message is ever in flight.
    private readonly Dictionary<string, int> _attempts = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await rabbit.GetAsync(stoppingToken);
                var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await Topology.DeclareAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 1, false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, delivery) => OnMessageAsync(channel, delivery, stoppingToken);
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, stoppingToken);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not start consuming {Queue}; retrying.", queue);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task OnMessageAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        try
        {
            await ProcessAsync(delivery, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            _attempts.Remove(delivery.BasicProperties.MessageId ?? string.Empty);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            var messageId = delivery.BasicProperties.MessageId ?? string.Empty;
            var attempts = _attempts.GetValueOrDefault(messageId) + 1;
            var retry = RetryPolicy.ShouldRetry(exception, attempts);
            logger.LogError(exception, "Failed to handle {RoutingKey} {MessageId} on attempt {Attempt}; {Outcome}.",
                delivery.RoutingKey, messageId, attempts, retry ? "retrying" : "dead-lettering");

            if (retry)
            {
                _attempts[messageId] = attempts;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            else
            {
                _attempts.Remove(messageId);
            }
            await channel.BasicNackAsync(delivery.DeliveryTag, false, requeue: retry, ct);
        }
    }

    private async Task ProcessAsync(BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var routingKey = delivery.RoutingKey;
        if (!_handlers.TryGetValue(routingKey, out var handler))
        {
            logger.LogWarning("No handler for {RoutingKey}; discarding.", routingKey);
            return;
        }

        var messageId = Guid.Parse(delivery.BasicProperties.MessageId!);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TDb>();

        if (await db.InboxMessages.AnyAsync(message => message.MessageId == messageId, ct))
        {
            return;
        }

        await handler.Invoke(scope.ServiceProvider, Encoding.UTF8.GetString(delivery.Body.Span), ct);
        db.InboxMessages.Add(new InboxMessage { MessageId = messageId, ProcessedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);

        foreach (var afterCommit in scope.ServiceProvider.GetServices<IAfterCommit>())
        {
            try
            {
                await afterCommit.RunAsync(ct);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "After-commit step failed for {MessageId}.", messageId);
            }
        }
    }
}

public sealed class MessagingBuilder<TDb>(IServiceCollection services, string queue)
    where TDb : DbContext, IMessagingDbContext
{
    public MessagingBuilder<TDb> Handle<TEvent, THandler>()
        where TEvent : IIntegrationEvent
        where THandler : class, IEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new HandlerRegistration(typeof(TEvent).Name, (provider, json, ct) =>
        {
            var message = JsonSerializer.Deserialize<TEvent>(json)
                ?? throw new InvalidOperationException($"Empty {typeof(TEvent).Name} payload.");
            return provider.GetRequiredService<THandler>().HandleAsync(message, ct);
        }));
        return this;
    }

    internal void AddConsumer() =>
        services.AddSingleton<IHostedService>(provider => ActivatorUtilities.CreateInstance<EventConsumer<TDb>>(provider, queue));
}

public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging<TDb>(
        this IServiceCollection services, string queue, Action<MessagingBuilder<TDb>> configure)
        where TDb : DbContext, IMessagingDbContext
    {
        services.AddSingleton<RabbitConnection>();
        services.AddHostedService<OutboxPublisher<TDb>>();
        var builder = new MessagingBuilder<TDb>(services, queue);
        configure(builder);
        builder.AddConsumer();
        return services;
    }
}
