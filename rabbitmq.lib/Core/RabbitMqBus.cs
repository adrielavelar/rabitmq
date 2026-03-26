using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMq.Pkg.Options;
using RabbitMq.Pkg.Serialization;
using System.Collections.Concurrent;

namespace RabbitMq.Pkg.Core;

/// <summary>
/// Implementação resiliente de bus RabbitMQ com pool de channels para publicação.
/// Mantém 1 connection singleton e reutiliza channels de publish via pool thread-safe.
/// Consumers recebem 1 channel exclusivo cada.
/// </summary>
public sealed class RabbitMqBus : IRabbitMqBus
{
    private readonly RabbitMqOptions _options;
    private readonly IRabbitMqSerializer _serializer;
    private readonly ILogger<RabbitMqBus>? _logger;

    private IConnection? _connection;

    // Pool de channels para publicação sem publisher confirms.
    // Cada slot é um (IChannel, SemaphoreSlim): o semáforo garante uso exclusivo do channel.
    private PooledChannel[]? _publishPool;

    // Channel dedicado para PublishWithConfirmAsync, criado com publisher confirms habilitados.
    private PooledChannel? _confirmChannel;

    /// <summary>Cria o bus com opções, serializador e logger.</summary>
    public RabbitMqBus(RabbitMqOptions options, IRabbitMqSerializer? serializer = null, ILogger<RabbitMqBus>? logger = null)
    {
        _options = options;
        _serializer = serializer ?? new JsonRabbitMqSerializer();
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Conexão
    // -------------------------------------------------------------------------

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
            factory.Ssl = new SslOption { Enabled = true };

        Exception? lastEx = null;
        foreach (var host in _options.Hosts.DefaultIfEmpty("localhost"))
        {
            try
            {
                factory.HostName = host;
                _connection = await factory.CreateConnectionAsync(cancellationToken);
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
            throw new InvalidOperationException("Não foi possível conectar a nenhum host configurado.", lastEx);

        _logger?.LogInformation("Conexão estabelecida: {Name}", _options.ClientProvidedName ?? "rabbitmq-bus");

        await InitializePoolAsync(cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Topologia (cada operação usa channel efêmero — ocorrência rara e não crítica)
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task DeclareExchangeAsync(string exchange, string type = "direct", bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await ch.ExchangeDeclareAsync(exchange, type, durable, autoDelete, arguments: args, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task DeclareQueueAsync(string queue, bool durable = true, bool exclusive = false, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await ch.QueueDeclareAsync(queue, durable, exclusive, autoDelete, arguments: args, passive: false, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task BindQueueAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
        await ch.QueueBindAsync(queue, exchange, routingKey, arguments: args, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task DeclareTopicExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
        => await DeclareExchangeAsync(exchange, ExchangeType.Topic, durable, autoDelete, args, ct);

    /// <inheritdoc />
    public async Task DeclareFanoutExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default)
        => await DeclareExchangeAsync(exchange, ExchangeType.Fanout, durable, autoDelete, args, ct);

    // -------------------------------------------------------------------------
    // Publicação — reutiliza channel do pool
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task PublishAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var slot = await AcquirePoolSlotAsync(ct);
        try
        {
            var ch = await EnsureChannelAliveAsync(slot, confirm: false, ct);

            var props = BuildProperties(configureProps);
            var body = _serializer.Serialize(message);

            await ch.BasicPublishAsync(exchange, routingKey, mandatory: false, basicProperties: props, body: body, cancellationToken: ct);
        }
        finally
        {
            slot.Semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task PublishWithConfirmAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var slot = _confirmChannel!;
        await slot.Semaphore.WaitAsync(ct);
        try
        {
            var ch = await EnsureChannelAliveAsync(slot, confirm: true, ct);

            var props = BuildProperties(configureProps);
            var body = _serializer.Serialize(message);

            await ch.BasicPublishAsync(exchange, routingKey, mandatory: true, basicProperties: props, body: body, cancellationToken: ct);
        }
        finally
        {
            slot.Semaphore.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Consumo — 1 channel exclusivo por consumidor
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<string> ConsumeAsync<T>(string queue, Func<T, BasicDeliverEventArgs, Task<AckStrategy>> handler, ushort prefetch = 50, bool autoAck = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Channel dedicado ao consumidor: não compartilhado com publicação
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
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, CancellationToken.None);
                        break;
                    case AckStrategy.NackRequeue:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
                        break;
                    case AckStrategy.NackDeadLetter:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Erro ao processar mensagem de {Queue}", queue);
                if (channel.IsOpen)
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, CancellationToken.None);
            }
        };

        var tag = await channel.BasicConsumeAsync(queue, autoAck, consumer: consumer, cancellationToken: ct);
        _logger?.LogInformation("Consumidor iniciado em {Queue} com tag {Tag}", queue, tag);

        // Background task responsável por encerrar o channel quando o CT for cancelado
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                try
                {
                    if (channel.IsOpen)
                    {
                        await channel.BasicCancelAsync(tag).ConfigureAwait(false);
                        await channel.CloseAsync().ConfigureAwait(false);
                    }
                    await channel.DisposeAsync().ConfigureAwait(false);
                    _logger?.LogInformation("Consumidor encerrado: {Tag}", tag);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Erro ao encerrar consumidor {Tag}", tag);
                }
            }
        }, CancellationToken.None);

        return tag;
    }

    // -------------------------------------------------------------------------
    // Health check e BasicGet
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<HealthStatus> CheckHealthAsync(CancellationToken ct = default)
    {
        try
        {
            await using var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
            var name = $"health-ex-{Guid.NewGuid():N}";
            await ch.ExchangeDeclareAsync(name, "fanout", durable: false, autoDelete: true, arguments: null, cancellationToken: ct);
            await ch.ExchangeDeleteAsync(name, ifUnused: false, cancellationToken: ct);
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
        return await ch.BasicGetAsync(queue, autoAck, cancellationToken: ct);
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_publishPool is not null)
        {
            foreach (var slot in _publishPool)
                await slot.DisposeAsync();
        }

        if (_confirmChannel is not null)
            await _confirmChannel.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    // -------------------------------------------------------------------------
    // Helpers privados
    // -------------------------------------------------------------------------

    private async Task InitializePoolAsync(CancellationToken ct)
    {
        var size = Math.Max(1, _options.PublishChannelPoolSize);
        _publishPool = new PooledChannel[size];

        for (int i = 0; i < size; i++)
        {
            var ch = await EnsureConnection().CreateChannelAsync(options: null, cancellationToken: ct);
            _publishPool[i] = new PooledChannel(ch);
        }

        var confirmOpts = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        var confirmCh = await EnsureConnection().CreateChannelAsync(options: confirmOpts, cancellationToken: ct);
        _confirmChannel = new PooledChannel(confirmCh);

        _logger?.LogInformation("Pool de publish inicializado com {Size} channel(s) + 1 confirm channel", size);
    }

    private async Task<PooledChannel> AcquirePoolSlotAsync(CancellationToken ct)
    {
        // Tenta adquirir o primeiro slot disponível (sem espera) antes de bloquear
        foreach (var slot in _publishPool!)
        {
            if (slot.Semaphore.CurrentCount > 0)
            {
                await slot.Semaphore.WaitAsync(ct);
                return slot;
            }
        }

        // Todos ocupados: aguarda o primeiro slot disponível usando round-robin via índice aleatório
        var idx = Random.Shared.Next(_publishPool.Length);
        await _publishPool[idx].Semaphore.WaitAsync(ct);
        return _publishPool[idx];
    }

    /// <summary>
    /// Verifica se o channel do slot está aberto. Se não estiver, recria e loga o evento.
    /// </summary>
    private async Task<IChannel> EnsureChannelAliveAsync(PooledChannel slot, bool confirm, CancellationToken ct)
    {
        if (slot.Channel.IsOpen)
            return slot.Channel;

        _logger?.LogWarning("Channel do pool fechado. Recriando...");

        await slot.Channel.DisposeAsync();

        CreateChannelOptions? opts = confirm
            ? new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)
            : null;

        slot.Channel = await EnsureConnection().CreateChannelAsync(options: opts, cancellationToken: ct);
        _logger?.LogInformation("Channel do pool recriado com sucesso");

        return slot.Channel;
    }

    private BasicProperties BuildProperties(Action<BasicProperties>? configure)
    {
        var props = new BasicProperties
        {
            ContentType = _serializer.ContentType,
            ContentEncoding = _serializer.ContentEncoding,
            DeliveryMode = DeliveryModes.Persistent
        };
        configure?.Invoke(props);
        return props;
    }

    private IConnection EnsureConnection()
    {
        if (_connection is null || !_connection.IsOpen)
            throw new InvalidOperationException("Conexão RabbitMQ não está aberta. Chame ConnectAsync primeiro.");
        return _connection;
    }

    /// <inheritdoc />
    public bool IsConnected => _connection is not null && _connection.IsOpen;

    // -------------------------------------------------------------------------
    // Tipos internos
    // -------------------------------------------------------------------------

    /// <summary>
    /// Representa um slot do pool: um channel com seu semáforo exclusivo (1 uso por vez).
    /// </summary>
    private sealed class PooledChannel : IAsyncDisposable
    {
        public IChannel Channel { get; set; }

        /// <summary>Garante acesso exclusivo ao channel (1 publicação por vez por slot).</summary>
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public PooledChannel(IChannel channel) => Channel = channel;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Channel.IsOpen)
                    await Channel.CloseAsync();
                await Channel.DisposeAsync();
            }
            catch { /* ignora erros no dispose */ }
            finally
            {
                Semaphore.Dispose();
            }
        }
    }
}
