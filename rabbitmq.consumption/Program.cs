using Microsoft.Extensions.Logging;
using RabbitMq.Pkg.Core;
using RabbitMq.Pkg.Options;
using RabbitMq.Pkg.Serialization;
using System.Diagnostics;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("consumption");

var opts = new RabbitMqOptions
{
    Hosts = new List<string> { Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost" },
    Port = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var p) ? p : 5672,
    VirtualHost = Environment.GetEnvironmentVariable("RABBITMQ_VHOST") ?? "/",
    Username = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? "guest",
    ClientProvidedName = "rabbitmq.consumption"
};

await using var bus = new RabbitMqBus(opts, new JsonRabbitMqSerializer(), loggerFactory.CreateLogger<RabbitMqBus>());
await bus.ConnectAsync();

var health = await bus.CheckHealthAsync();
logger.LogInformation("Health: {Healthy} - {Msg}", health.IsHealthy, health.Message);

const string ex = "demo.exchange";
const string q = "demo.queue";
const string dlx = "demo.dlx";
const string dlq = "demo.deadletter";

await bus.DeclareExchangeAsync(ex, "direct", durable: true);
await bus.DeclareExchangeAsync(dlx, "direct", durable: true);

var queueArgs = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = dlx, ["x-dead-letter-routing-key"] = dlq };
await bus.DeclareQueueAsync(q, durable: true, args: queueArgs);
await bus.DeclareQueueAsync(dlq, durable: true);
await bus.BindQueueAsync(q, ex, routingKey: "demo-key");
await bus.BindQueueAsync(dlq, dlx, routingKey: dlq);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Contadores para estatísticas
int processedCount = 0;
int failedCount = 0;
var processingLock = new object();

_ = bus.ConsumeAsync<DemoMessage>(q, async (msg, ea) =>
{
    try
    {
        // Log detalhado da deserialização para algumas mensagens da fila
        lock (processingLock)
        {
            if (processedCount < 5 || processedCount % 1000 == 0)
            {
                logger.LogInformation("Mensagem deserializada -> Id: {Id}, Payload: {Payload}, Timestamp: {Timestamp}, Status: {Status}",
                    msg.Id, msg.Payload, msg.Timestamp, msg.Status);
            }
        }

        // Simula cenário de falha para testar Dead Letter
        if (msg.Payload.Contains("nack", StringComparison.OrdinalIgnoreCase))
        {
            lock (processingLock) failedCount++;
            logger.LogWarning("Mensagem {Id} rejeitada (nack) - indo para DLQ", msg.Id);
            return AckStrategy.NackDeadLetter;
        }

        await Task.Delay(1);

        lock (processingLock)
        {
            processedCount++;
            if (processedCount % 1000 == 0)
            {
                logger.LogInformation("Processadas {Count} mensagens até agora", processedCount);
            }
        }

        return AckStrategy.Ack;
    }
    catch (Exception ex)
    {
        lock (processingLock) failedCount++;
        logger.LogError(ex, "Erro ao processar mensagem {Id}", msg.Id);
        return AckStrategy.NackRequeue;
    }
}, prefetch: 100, ct: cts.Token);

// Smoke test: publica 10 mensagens com JSON válido e estruturado
logger.LogInformation("Iniciando smoke test com 10 mensagens estruturadas...");
for (int i = 0; i < 10; i++)
{
    var message = new DemoMessage
    {
        Id = i,
        Payload = $"hello-{i}",
        Timestamp = DateTime.UtcNow,
        Status = i % 2 == 0 ? "even" : "odd"
    };
    await bus.PublishWithConfirmAsync(ex, "demo-key", message);
    logger.LogInformation("Publicada: Id={Id}, Payload={Payload}", message.Id, message.Payload);
}
logger.LogInformation("Smoke test publicou 10 mensagens com confirmação");

int total = int.TryParse(Environment.GetEnvironmentVariable("STRESS_TOTAL"), out var st) ? st : 100000;
int parallel = int.TryParse(Environment.GetEnvironmentVariable("STRESS_PARALLEL"), out var sp) ? sp : Environment.ProcessorCount;

logger.LogInformation("Iniciando stress test com {Total} mensagens em {Parallel} workers...", total, parallel);
var sw = Stopwatch.StartNew();
var tasks = Enumerable.Range(0, parallel).Select(worker => Task.Run(async () =>
{
    for (int i = worker; i < total; i += parallel)
    {
        var message = new DemoMessage
        {
            Id = i,
            Payload = $"bulk-{i}",
            Timestamp = DateTime.UtcNow,
            Status = "pending"
        };
        await bus.PublishAsync(ex, "demo-key", message);
    }
}));

await Task.WhenAll(tasks);
sw.Stop();
logger.LogInformation("Stress test publicou {Total} mensagens JSON em {Ms} ms (≈ {Rate}/s)",
    total, sw.ElapsedMilliseconds, (int)(total / sw.Elapsed.TotalSeconds));

Console.WriteLine("\n=== Aguardando processamento das mensagens ===");
Console.WriteLine("Pressione Ctrl+C para encerrar...\n");

// Aguarda o processamento das mensagens
var timeout = TimeSpan.FromMinutes(20);
var processingStart = Stopwatch.StartNew();

try
{
    while (!cts.Token.IsCancellationRequested && processingStart.Elapsed < timeout)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

        int currentProcessed, currentFailed;
        lock (processingLock)
        {
            currentProcessed = processedCount;
            currentFailed = failedCount;
        }

        logger.LogInformation("Status: {Processed} processadas | {Failed} falhas | Tempo: {Elapsed:mm\\:ss}",
            currentProcessed, currentFailed, processingStart.Elapsed);

        // Se todas as mensagens esperadas foram processadas, encerra
        // if (currentProcessed + currentFailed >= total + 10)
        // {
        //     logger.LogInformation("Todas as mensagens foram processadas!");
        //     break;
        // }
    }
}
catch (OperationCanceledException)
{
    logger.LogInformation("Consumidor interrompido pelo usuário");
}

processingStart.Stop();
logger.LogInformation("\n=== Estatísticas Finais ===");
logger.LogInformation("Total processadas com sucesso: {Processed}", processedCount);
logger.LogInformation("Total com falha: {Failed}", failedCount);
logger.LogInformation("Tempo total de processamento: {Elapsed:mm\\:ss}", processingStart.Elapsed);

/// <summary>
/// Mensagem de demonstração serializada/deserializada como JSON pelo JsonRabbitMqSerializer.
/// </summary>
public sealed class DemoMessage
{
    /// <summary>Identificador único da mensagem.</summary>
    public int Id { get; set; }

    /// <summary>Conteúdo/corpo da mensagem.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Timestamp UTC de quando a mensagem foi criada.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Status ou tipo da mensagem (ex: "pending", "even", "odd").</summary>
    public string Status { get; set; } = "unknown";
}