using System.Text.Json;

namespace RabbitMq.Pkg.Serialization;

/// <summary>
/// Serializador JSON baseado em System.Text.Json.
/// </summary>
public sealed class JsonRabbitMqSerializer : IRabbitMqSerializer
{
    private readonly JsonSerializerOptions _opts;

    /// <summary>Inicializa com opções fornecidas ou padrão.</summary>
    public JsonRabbitMqSerializer(JsonSerializerOptions? options = null)
    {
        _opts = options ?? new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, _opts);

    /// <inheritdoc />
    public T Deserialize<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload, _opts)!;

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public string ContentEncoding => "utf-8";
}