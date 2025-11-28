# RabbitMQ.Pkg

Biblioteca .NET para integração simplificada com RabbitMQ, oferecendo APIs de alto nível para conexão, publicação e consumo de mensagens com suporte a serialização customizável.

## Instalação

Adicione a referência ao projeto:

```bash
dotnet add package RabbitMQ.Pkg
```

Ou através do arquivo `.csproj`:

```xml
<PackageReference Include="RabbitMQ.Pkg" Version="0.1.0" />
```

## Configuração Inicial

### RabbitMqOptions

A classe `RabbitMqOptions` centraliza todas as configurações de conexão:

```csharp
using RabbitMq.Pkg.Options;

var options = new RabbitMqOptions
{
    Hosts = new List<string> { "localhost" },
    Port = 5672,
    VirtualHost = "/",
    Username = "guest",
    Password = "guest",
    ClientProvidedName = "meu-servico",
    UseSsl = false,
    RequestedHeartbeat = TimeSpan.FromSeconds(30),
    AutomaticRecoveryEnabled = true,
    TopologyRecoveryEnabled = true,
    NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
    PublishConfirmTimeout = TimeSpan.FromSeconds(10)
};
```

### Propriedades Disponíveis

| Propriedade | Tipo | Padrão | Descrição |
|------------|------|--------|-----------|
| `Hosts` | `IList<string>` | `["localhost"]` | Lista de hosts do broker. Suporta clusters |
| `Port` | `int` | `5672` | Porta AMQP |
| `VirtualHost` | `string` | `"/"` | Virtual host do RabbitMQ |
| `Username` | `string` | `"guest"` | Usuário de autenticação |
| `Password` | `string` | `"guest"` | Senha de autenticação |
| `ClientProvidedName` | `string?` | `null` | Nome de identificação do cliente |
| `UseSsl` | `bool` | `false` | Habilita conexão TLS/SSL |
| `RequestedHeartbeat` | `TimeSpan?` | `30s` | Intervalo de heartbeat |
| `AutomaticRecoveryEnabled` | `bool` | `true` | Recuperação automática de conexão |
| `TopologyRecoveryEnabled` | `bool` | `true` | Redeclara exchanges/queues após recuperação |
| `NetworkRecoveryInterval` | `TimeSpan` | `10s` | Intervalo de tentativas de reconexão |
| `PublishConfirmTimeout` | `TimeSpan` | `10s` | Timeout para publisher confirms |

## Serialização

A biblioteca utiliza a interface `IRabbitMqSerializer` para serializar e deserializar mensagens.

### IRabbitMqSerializer

```csharp
public interface IRabbitMqSerializer
{
    byte[] Serialize<T>(T value);
    T Deserialize<T>(ReadOnlySpan<byte> payload);
    string ContentType { get; }
    string ContentEncoding { get; }
}
```

### JsonRabbitMqSerializer

Implementação padrão utilizando `System.Text.Json`:

```csharp
using RabbitMq.Pkg.Serialization;

var serializer = new JsonRabbitMqSerializer(new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
});
```

O `JsonRabbitMqSerializer` já é o padrão quando nenhum serializador é fornecido.

### Implementação Customizada

```csharp
public class CustomSerializer : IRabbitMqSerializer
{
    public byte[] Serialize<T>(T value)
    {
        // Implementação customizada
    }

    public T Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        // Implementação customizada
    }

    public string ContentType => "application/custom";
    public string ContentEncoding => "utf-8";
}
```

## Uso Básico

### Inicialização

```csharp
using Microsoft.Extensions.Logging;
using RabbitMq.Pkg.Core;
using RabbitMq.Pkg.Options;
using RabbitMq.Pkg.Serialization;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole().SetMinimumLevel(LogLevel.Information);
});

var options = new RabbitMqOptions
{
    Hosts = new List<string> { "localhost" },
    ClientProvidedName = "meu-servico"
};

await using var bus = new RabbitMqBus(
    options,
    new JsonRabbitMqSerializer(),
    loggerFactory.CreateLogger<RabbitMqBus>()
);

await bus.ConnectAsync();
```

### Verificação de Saúde

```csharp
var health = await bus.CheckHealthAsync();

if (health.IsHealthy)
{
    Console.WriteLine($"Broker saudável: {health.Message}");
}
```

## Gerenciamento de Topologia

### Declaração de Exchange

```csharp
// Exchange Direct
await bus.DeclareExchangeAsync("minha.exchange", "direct", durable: true);

// Exchange Topic
await bus.DeclareTopicExchangeAsync("eventos.topic", durable: true);

// Exchange Fanout
await bus.DeclareFanoutExchangeAsync("broadcast.exchange", durable: true);
```

### Declaração de Fila

```csharp
// Fila simples
await bus.DeclareQueueAsync("minha.fila", durable: true);

// Fila com Dead Letter Exchange
var queueArgs = new Dictionary<string, object?>
{
    ["x-dead-letter-exchange"] = "dlx.exchange",
    ["x-dead-letter-routing-key"] = "dlq",
    ["x-message-ttl"] = 60000
};

await bus.DeclareQueueAsync("minha.fila", durable: true, args: queueArgs);
```

### Binding

```csharp
await bus.BindQueueAsync(
    queue: "minha.fila",
    exchange: "minha.exchange",
    routingKey: "minha.chave"
);
```

## Publicação de Mensagens

### Publicação Simples

```csharp
public class MinhaMsg
{
    public int Id { get; set; }
    public string Conteudo { get; set; }
}

var mensagem = new MinhaMsg { Id = 1, Conteudo = "Olá, RabbitMQ!" };

await bus.PublishAsync("minha.exchange", "minha.chave", mensagem);
```

### Publicação com Confirmação

```csharp
// Aguarda confirmação do broker (publisher confirm)
await bus.PublishWithConfirmAsync("minha.exchange", "minha.chave", mensagem);
```

### Configuração de Propriedades

```csharp
await bus.PublishAsync("minha.exchange", "minha.chave", mensagem, props =>
{
    props.MessageId = Guid.NewGuid().ToString();
    props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    props.Persistent = true;
    props.Priority = 5;
    props.Headers = new Dictionary<string, object>
    {
        ["custom-header"] = "valor"
    };
});
```

## Consumo de Mensagens

### Consumidor Básico

```csharp
var consumerTag = await bus.ConsumeAsync<MinhaMsg>(
    queue: "minha.fila",
    handler: async (mensagem, deliveryArgs) =>
    {
        Console.WriteLine($"Processando: {mensagem.Conteudo}");
        
        // Simula processamento
        await Task.Delay(100);
        
        return AckStrategy.Ack;
    },
    prefetch: 100
);
```

### Estratégias de Confirmação

A enum `AckStrategy` define o comportamento após processar uma mensagem:

| Estratégia | Descrição |
|-----------|-----------|
| `Ack` | Remove a mensagem da fila (sucesso) |
| `NackRequeue` | Devolve a mensagem para a fila |
| `NackDeadLetter` | Envia para Dead Letter Exchange (se configurado) |

### Consumidor com Tratamento de Erros

```csharp
await bus.ConsumeAsync<MinhaMsg>("minha.fila", async (msg, ea) =>
{
    try
    {
        if (msg.Conteudo.Contains("falha"))
        {
            return AckStrategy.NackDeadLetter;
        }
        
        await ProcessarMensagem(msg);
        return AckStrategy.Ack;
    }
    catch (RecuperavelException)
    {
        return AckStrategy.NackRequeue;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Erro irrecuperável");
        return AckStrategy.NackDeadLetter;
    }
}, prefetch: 50);
```

### Consumidor com Cancelamento

```csharp
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await bus.ConsumeAsync<MinhaMsg>("minha.fila", async (msg, ea) =>
{
    await ProcessarMensagem(msg);
    return AckStrategy.Ack;
}, prefetch: 100, ct: cts.Token);

// Aguarda até cancelamento
await Task.Delay(Timeout.Infinite, cts.Token);
```

## Obtenção Síncrona de Mensagem

```csharp
var result = await bus.BasicGetAsync("minha.fila", autoAck: false);

if (result != null)
{
    var mensagem = serializer.Deserialize<MinhaMsg>(result.Body.Span);
    Console.WriteLine($"Mensagem obtida: {mensagem.Conteudo}");
    
    // Manual ACK
    // channel.BasicAck(result.DeliveryTag, false);
}
```

## Padrões de Uso

### Dead Letter Queue

```csharp
const string exchange = "pedidos.exchange";
const string queue = "pedidos.queue";
const string dlx = "pedidos.dlx";
const string dlq = "pedidos.dlq";

// Declara exchanges
await bus.DeclareExchangeAsync(exchange, "direct", durable: true);
await bus.DeclareExchangeAsync(dlx, "direct", durable: true);

// Declara filas com DLX
var queueArgs = new Dictionary<string, object?>
{
    ["x-dead-letter-exchange"] = dlx,
    ["x-dead-letter-routing-key"] = dlq
};

await bus.DeclareQueueAsync(queue, durable: true, args: queueArgs);
await bus.DeclareQueueAsync(dlq, durable: true);

// Bindings
await bus.BindQueueAsync(queue, exchange, "pedidos");
await bus.BindQueueAsync(dlq, dlx, dlq);

// Consumidor com DLQ
await bus.ConsumeAsync<Pedido>(queue, async (pedido, ea) =>
{
    if (!pedido.EhValido())
    {
        return AckStrategy.NackDeadLetter; // Vai para DLQ
    }
    
    await ProcessarPedido(pedido);
    return AckStrategy.Ack;
}, prefetch: 50);
```

### Publicação em Lote

```csharp
int total = 10000;
int workers = Environment.ProcessorCount;

var tasks = Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
{
    for (int i = worker; i < total; i += workers)
    {
        var msg = new MinhaMsg { Id = i, Conteudo = $"Mensagem {i}" };
        await bus.PublishAsync("minha.exchange", "chave", msg);
    }
}));

await Task.WhenAll(tasks);
```

### Topic Exchange

```csharp
await bus.DeclareTopicExchangeAsync("logs.topic", durable: true);

await bus.DeclareQueueAsync("logs.erro");
await bus.DeclareQueueAsync("logs.todos");

// Captura apenas erros
await bus.BindQueueAsync("logs.erro", "logs.topic", "*.erro");

// Captura todos os logs
await bus.BindQueueAsync("logs.todos", "logs.topic", "#");

// Publicação
await bus.PublishAsync("logs.topic", "app.erro", new { Mensagem = "Erro crítico" });
await bus.PublishAsync("logs.topic", "app.info", new { Mensagem = "Informação" });
```

## Boas Práticas

### Gerenciamento de Conexão

```csharp
// Use await using para garantir dispose correto
await using var bus = new RabbitMqBus(options, serializer, logger);
await bus.ConnectAsync();

// Suas operações...
```

### Prefetch

```csharp
// Baixo prefetch para processamento lento
await bus.ConsumeAsync<MsgPesada>("fila.lenta", handler, prefetch: 10);

// Alto prefetch para processamento rápido
await bus.ConsumeAsync<MsgRapida>("fila.rapida", handler, prefetch: 500);
```

### Durabilidade

```csharp
// Exchanges e filas duráveis sobrevivem a reinicializações
await bus.DeclareExchangeAsync("minha.exchange", "direct", durable: true);
await bus.DeclareQueueAsync("minha.fila", durable: true);

// Mensagens persistentes
await bus.PublishAsync("minha.exchange", "chave", mensagem, props =>
{
    props.Persistent = true;
});
```

### Timeouts

```csharp
var options = new RabbitMqOptions
{
    PublishConfirmTimeout = TimeSpan.FromSeconds(5),
    RequestedHeartbeat = TimeSpan.FromSeconds(60)
};
```

## Troubleshooting

### Conexão Falha

Verifique:

- Host e porta corretos
- Credenciais válidas
- Virtual host existente
- Firewall/rede permitindo conexão

### Mensagens Não São Consumidas

Verifique:

- Binding entre exchange e fila está correto
- Routing key corresponde ao esperado
- Fila foi declarada corretamente
- Consumidor está ativo e sem erros

### Mensagens na DLQ

Causas comuns:

- Exceções não tratadas no handler
- Retorno de `AckStrategy.NackDeadLetter`
- TTL expirado
- Limite de redelivery atingido

## Referências

- [RabbitMQ Documentation](https://www.rabbitmq.com/documentation.html)
- [AMQP 0-9-1 Protocol](https://www.rabbitmq.com/tutorials/amqp-concepts.html)
- [RabbitMQ .NET Client](https://github.com/rabbitmq/rabbitmq-dotnet-client)

## Licença

Este projeto está licenciado sob os termos da licença MIT.
