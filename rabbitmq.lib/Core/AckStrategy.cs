namespace RabbitMq.Pkg.Core;

/// <summary>
/// Estratégia de confirmação após processamento de mensagem.
/// </summary>
public enum AckStrategy
{
    /// <summary>Ack: remove a mensagem da fila.</summary>
    Ack,
    /// <summary>Nack com requeue: devolve à fila.</summary>
    NackRequeue,
    /// <summary>Nack sem requeue: envia para DLX quando configurado.</summary>
    NackDeadLetter
}