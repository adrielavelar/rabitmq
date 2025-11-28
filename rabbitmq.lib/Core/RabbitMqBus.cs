using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMq.Pkg.Options;
using RabbitMq.Pkg.Serialization;
using System.Linq;

namespace RabbitMq.Pkg.Core;

/// <summary>
/// Implementação resiliente de bus RabbitMQ: conexão, topologia, publicação e consumo.
/// </summary>
public sealed class RabbitMqBus : IRabbitMqBus
{
    private readonly RabbitMqOptions _options;
    private readonly IRabbitMqSerializer _serializer;
    private readonly ILogger<RabbitMqBus>? _logger;
    private IConnection? _connection;

    /// <summary>Cria o bus com opções, serializador e logger.</summary>
    public RabbitMqBus(RabbitMqOptions options, IRabbitMqSerializer? serializer = null, ILogger<RabbitMqBus>? logger = null)
    {
        _options = options;
        _serializer = serializer ?? new JsonRabbitMqSerializer();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            VirtualHost = _options.VirtualHost,
            UserName = _options.Username,
            Password = _options.Password,
            RequestedHeartbeat = _options.RequestedHeartbeat ?? TimeSpan.FromSeconds(30),
            AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
            TopologyRecoveryEnabled = _options.TopologyRecoveryEnabled,
            NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
            ClientProvidedName = _options.ClientProvidedName,
            Port = _options.Port
        };

        if (_options.UseSsl)
        {
            factory.Ssl = new SslOption { Enabled = true };
        }

        Exception? lastEx = null;
        foreach (var host in _options.Hosts.DefaultIfEmpty("localhost"))
        {
            try
            {
                factory.HostName = host;
                _connection = await factory.CreateConnectionAsync();
                _logger?.LogInformation("Conectado ao RabbitMQ em {Host}", host);
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger?.LogWarning(ex, "Falha ao conectar em {Host}", host);
            }
        }
        if (_connection is null || !_connection.IsOpen)
        {
            throw new InvalidOperationException("Não foi possível conectar a nenhum host configurado", lastEx);
        }
        _logger?.LogInformation("Conectado ao RabbitMQ: {Name}", _options.ClientProvidedName ?? "rabbitmq-bus");
    }

    /// <inheritdoc />
    public async Task DeclareExchangeAsync(string exchange, string type = "direct", bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var channel = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(exchange, type, durable, autoDelete, arguments: args);
    }

    /// <inheritdoc />
    public async Task DeclareQueueAsync(string queue, bool durable = true, bool exclusive = false, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var channel = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable, exclusive, autoDelete, arguments: args, passive: false);
    }

    /// <inheritdoc />
    public async Task BindQueueAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var channel = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await channel.QueueBindAsync(queue, exchange, routingKey, arguments: args);
    }

    /// <summary>
    /// Declara uma exchange do tipo <c>topic</c>, útil para roteamento por padrões de chave.
    /// </summary>
    public async Task DeclareTopicExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        await DeclareExchangeAsync(exchange, RabbitMQ.Client.ExchangeType.Topic, durable, autoDelete, args, ct);
    }

    /// <summary>
    /// Declara uma exchange do tipo <c>fanout</c>, útil para broadcast sem chave de roteamento.
    /// </summary>
    public async Task DeclareFanoutExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        await DeclareExchangeAsync(exchange, RabbitMQ.Client.ExchangeType.Fanout, durable, autoDelete, args, ct);
    }

    /// <inheritdoc />
    public async Task PublishAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var channel = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        var props = new BasicProperties
        {
            ContentType = _serializer.ContentType,
            ContentEncoding = _serializer.ContentEncoding,
            DeliveryMode = RabbitMQ.Client.DeliveryModes.Persistent
        };
        configureProps?.Invoke(props);

        var body = _serializer.Serialize(message);
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: false, basicProperties: props, body: body, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task PublishWithConfirmAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var channelOpts = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var channel = await EnsureConnection().CreateChannelAsync(options: channelOpts, cancellationToken: ct);

        var props = new BasicProperties
        {
            ContentType = _serializer.ContentType,
            ContentEncoding = _serializer.ContentEncoding,
            DeliveryMode = RabbitMQ.Client.DeliveryModes.Persistent
        };
        configureProps?.Invoke(props);

        var body = _serializer.Serialize(message);
        await channel.BasicPublishAsync(exchange, routingKey, mandatory: true, basicProperties: props, body: body, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<string> ConsumeAsync<T>(string queue, Func<T, BasicDeliverEventArgs, Task<AckStrategy>> handler, ushort prefetch = 50, bool autoAck = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var channel = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await channel.BasicQosAsync(0, prefetch, global: false, cancellationToken: ct);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            try
            {
                var msg = _serializer.Deserialize<T>(ea.Body.Span);
                var strategy = await handler(msg, ea).ConfigureAwait(false);
                if (!channel.IsOpen) return;
                switch (strategy)
                {
                    case AckStrategy.Ack:
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
                        break;
                    case AckStrategy.NackRequeue:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, ct);
                        break;
                    case AckStrategy.NackDeadLetter:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao processar mensagem de {Queue}", queue);
                if (channel.IsOpen)
                {
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
                }
            }
            return;
        };

        var tag = await channel.BasicConsumeAsync(queue, autoAck, consumer: consumer, cancellationToken: ct);
        _logger?.LogInformation("Consumidor iniciado em {Queue} com tag {Tag}", queue, tag);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested) await Task.Delay(250, ct).ConfigureAwait(false);
                await channel.BasicCancelAsync(tag, cancellationToken: ct);
                await channel.CloseAsync(ct);
                await channel.DisposeAsync();
                _logger?.LogInformation("Consumidor encerrado {Tag}", tag);
            }
            catch (OperationCanceledException) { }
        }, ct);

        return tag;
    }

    /// <inheritdoc />
    public async Task<HealthStatus> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
            var name = $"health-ex-{Guid.NewGuid():N}";
            await ch.ExchangeDeclareAsync(name, "fanout", durable: false, autoDelete: true, arguments: null);
            await ch.ExchangeDeleteAsync(name, ifUnused: false);
            return new HealthStatus(true, "Broker RabbitMQ acessível");
        }
        catch (Exception ex)
        {
            return new HealthStatus(false, $"Health check falhou: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        var res = await ch.BasicGetAsync(queue, autoAck, cancellationToken: ct);
        return res;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            return _connection.DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }

    private IConnection EnsureConnection()
    {
        if (_connection is null || !_connection.IsOpen)
            throw new InvalidOperationException("Conexão RabbitMQ não está aberta. Chame ConnectAsync primeiro.");
        return _connection;
    }
}