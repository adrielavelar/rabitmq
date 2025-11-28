namespace RabbitMq.Pkg.Options;

/// <summary>
/// Opções de conexão e comportamento do cliente RabbitMQ.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>Lista de hosts do broker. Suporta clusters.</summary>
    public IList<string> Hosts { get; init; } = new List<string> { "localhost" };
    /// <summary>Porta AMQP. Padrão: 5672.</summary>
    public int Port { get; init; } = 5672;
    /// <summary>Virtual host. Padrão: "/".</summary>
    public string VirtualHost { get; init; } = "/";
    /// <summary>Usuário. Padrão: "guest".</summary>
    public string Username { get; init; } = "guest";
    /// <summary>Senha. Padrão: "guest".</summary>
    public string Password { get; init; } = "guest";
    /// <summary>Nome de conexão fornecido pelo cliente.</summary>
    public string? ClientProvidedName { get; init; }
    /// <summary>Habilita TLS/SSL.</summary>
    public bool UseSsl { get; init; } = false;
    /// <summary>Intervalo de heartbeat solicitado.</summary>
    public TimeSpan? RequestedHeartbeat { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Recuperação automática de conexão.</summary>
    public bool AutomaticRecoveryEnabled { get; init; } = true;
    /// <summary>Recuperação de topologia (redeclara exchanges/queues após recuperação).</summary>
    public bool TopologyRecoveryEnabled { get; init; } = true;
    /// <summary>Intervalo de recuperação de rede.</summary>
    public TimeSpan NetworkRecoveryInterval { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Timeout padrão para publisher confirms.</summary>
    public TimeSpan PublishConfirmTimeout { get; init; } = TimeSpan.FromSeconds(10);
}