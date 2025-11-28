namespace RabbitMq.Pkg.Serialization;

/// <summary>
/// Abstração de serialização de mensagens.
/// </summary>
public interface IRabbitMqSerializer
{
    /// <summary>Serializa um valor para binário.</summary>
    byte[] Serialize<T>(T value);
    /// <summary>Deserializa um binário para o tipo informado.</summary>
    T Deserialize<T>(ReadOnlySpan<byte> payload);
    /// <summary>Content-Type associado às publicações.</summary>
    string ContentType { get; }
    /// <summary>Content-Encoding associado às publicações.</summary>
    string ContentEncoding { get; }
}