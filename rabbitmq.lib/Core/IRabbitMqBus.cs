using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RabbitMq.Pkg.Core;

/// <summary>
/// API de alto nível para operações diárias com RabbitMQ.
/// </summary>
public interface IRabbitMqBus : IAsyncDisposable
{
    /// <summary>Conecta ao RabbitMQ usando as opções configuradas.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Indica se a connection subjacente está aberta e operacional.</summary>
    bool IsConnected { get; }

    /// <summary>Declara uma exchange caso não exista.</summary>
    Task DeclareExchangeAsync(string exchange, string type = "direct", bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default);

    /// <summary>Declara uma fila caso não exista.</summary>
    Task DeclareQueueAsync(string queue, bool durable = true, bool exclusive = false, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default);

    /// <summary>Cria o binding entre fila e exchange.</summary>
    Task BindQueueAsync(string queue, string exchange, string routingKey, IDictionary<string, object?>? args = null, CancellationToken ct = default);

    /// <summary>Declara uma exchange do tipo topic.</summary>
    Task DeclareTopicExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default);

    /// <summary>Declara uma exchange do tipo fanout.</summary>
    Task DeclareFanoutExchangeAsync(string exchange, bool durable = true, bool autoDelete = false, IDictionary<string, object?>? args = null, CancellationToken ct = default);

    /// <summary>Publica mensagem sem confirmação.</summary>
    Task PublishAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default);

    /// <summary>Publica mensagem aguardando publisher confirm.</summary>
    Task PublishWithConfirmAsync<T>(string exchange, string routingKey, T message, Action<BasicProperties>? configureProps = null, CancellationToken ct = default);

    /// <summary>Consome mensagens com handler que define a estratégia de ACK/NACK.</summary>
    Task<string> ConsumeAsync<T>(string queue, Func<T, BasicDeliverEventArgs, Task<AckStrategy>> handler, ushort prefetch = 50, bool autoAck = false, CancellationToken ct = default);

    /// <summary>Executa verificação de saúde do broker.</summary>
    Task<HealthStatus> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>Obtém uma única mensagem de forma síncrona (envolta em Task).</summary>
    Task<BasicGetResult?> BasicGetAsync(string queue, bool autoAck = false, CancellationToken ct = default);
}