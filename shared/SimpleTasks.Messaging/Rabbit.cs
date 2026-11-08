using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;
using SimpleTasks.Contracts;

namespace SimpleTasks.Messaging;

/// <summary>
/// Every service declares the whole topology, so an event published before its
/// consumer has started still lands in that consumer's durable queue.
/// </summary>
public static class Topology
{
    public const string Exchange = "simpletasks.events";
    public const string DeadLetterExchange = "simpletasks.dead";
    public const string DeadLetterQueue = "simpletasks.dead";
    public const int DeliveryLimit = 10;

    public static readonly IReadOnlyDictionary<string, string[]> Subscriptions = new Dictionary<string, string[]>
    {
        ["identity"] = [nameof(ProjectInviteRequested)],
        ["work"] = [nameof(UserChanged), nameof(ProjectMembershipGranted)],
        ["notifications"] =
        [
            nameof(UserChanged), nameof(InviteCreated), nameof(PasswordResetRequested), nameof(TicketChanged), nameof(TicketAssigned),
            nameof(CommentAdded), nameof(EstimateChanged), nameof(EstimateThresholdCrossed)
        ]
    };

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, string.Empty, cancellationToken: ct);

        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = DeliveryLimit,
            ["x-dead-letter-exchange"] = DeadLetterExchange
        };
        foreach (var (queue, routingKeys) in Subscriptions)
        {
            await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: ct);
            foreach (var routingKey in routingKeys)
            {
                await channel.QueueBindAsync(queue, Exchange, routingKey, cancellationToken: ct);
            }
        }
    }
}

public sealed class RabbitConnection(IConfiguration configuration) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is null)
            {
                var uri = configuration.GetConnectionString("messaging")
                    ?? throw new InvalidOperationException("The messaging connection string is missing.");
                var factory = new ConnectionFactory { Uri = new Uri(uri), AutomaticRecoveryEnabled = true };
                _connection = await factory.CreateConnectionAsync(ct);
            }
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
