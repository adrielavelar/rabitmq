namespace RabbitMq.Pkg.Core;

/// <summary>
/// Resultado de verificação de saúde do broker.
/// </summary>
public sealed class HealthStatus
{
    /// <summary>Cria uma instância de status de saúde.</summary>
    public HealthStatus(bool isHealthy, string message)
    {
        IsHealthy = isHealthy;
        Message = message;
    }

    /// <summary>Indica se operações no broker estão saudáveis.</summary>
    public bool IsHealthy { get; }
    /// <summary>Mensagem detalhada do resultado.</summary>
    public string Message { get; }
}