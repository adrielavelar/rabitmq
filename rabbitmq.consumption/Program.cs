using Microsoft.Extensions.Logging;
using RabbitMq.Pkg.Core;
using RabbitMq.Pkg.Options;
using RabbitMq.Pkg.Serialization;
using System.Diagnostics;

// ---------------------------------------------------------------------------
// Configuração de logging
// ---------------------------------------------------------------------------
var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("bombardier");

// ---------------------------------------------------------------------------
// Parâmetros via variável de ambiente
// ---------------------------------------------------------------------------
string mode = Environment.GetEnvironmentVariable("MODE") ?? "stress"; // "stress" | "server"
int stressTotal = Env("STRESS_TOTAL", 500_000);
int stressParallel = Env("STRESS_PARALLEL", Environment.ProcessorCount);
int prefetch = Env("STRESS_PREFETCH", 500);
int poolSize = Env("STRESS_POOL_SIZE", 8);
int batchReport = Env("STRESS_BATCH_REPORT", 10_000);
// Modo servidor: publica N msgs a cada intervalo
int serverBatch = Env("SERVER_BATCH", 100);   // mensagens por burst
int serverInterval = Env("SERVER    _INTERVAL_MS", 1_000); // intervalo em ms

logger.LogInformation("=== Configuracao ===");
logger.LogInformation("  Modo               : {Mode}", mode);
logger.LogInformation("  Pool de channels   : {Pool}", poolSize);
logger.LogInformation("  Prefetch           : {Prefetch}", prefetch);
if (mode == "stress")
{
    logger.LogInformation("  Total de mensagens : {Total}", stressTotal);
    logger.LogInformation("  Workers de publish : {Workers}", stressParallel);
}
else
{
    logger.LogInformation("  Batch por burst    : {Batch}", serverBatch);
    logger.LogInformation("  Intervalo (ms)     : {Interval}", serverInterval);
}

// ---------------------------------------------------------------------------
// Conexão — singleton, fica aberta durante toda a vida da aplicação
// ---------------------------------------------------------------------------
var opts = new RabbitMqOptions
{
    Hosts = [Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost"],
    Port = Env("RABBITMQ_PORT", 5672),
    VirtualHost = Environment.GetEnvironmentVariable("RABBITMQ_VHOST") ?? "/",
    Username = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest",
    Password = Environment.GetEnvironmentVariable("RABBITMQ_PASS") ?? "guest",
    ClientProvidedName = $"rabbitmq.{mode}",
    PublishChannelPoolSize = poolSize,
};

await using var bus = new RabbitMqBus(
    opts,
    new JsonRabbitMqSerializer(),
    loggerFactory.CreateLogger<RabbitMqBus>());

await bus.ConnectAsync();

var health = await bus.CheckHealthAsync();
logger.LogInformation("Health: {Healthy} - {Msg}", health.IsHealthy, health.Message);
if (!health.IsHealthy) return;

// ---------------------------------------------------------------------------
// Topologia
// ---------------------------------------------------------------------------
const string exchange = "bombardier.exchange";
const string queue = "bombardier.queue";
const string dlx = "bombardier.dlx";
const string dlq = "bombardier.dlq";

await bus.DeclareExchangeAsync(exchange, "direct", durable: true);
await bus.DeclareExchangeAsync(dlx, "direct", durable: true);

var queueArgs = new Dictionary<string, object?>
{
    ["x-dead-letter-exchange"] = dlx,
    ["x-dead-letter-routing-key"] = dlq,
};
await bus.DeclareQueueAsync(queue, durable: true, args: queueArgs);
await bus.DeclareQueueAsync(dlq, durable: true);
await bus.BindQueueAsync(queue, exchange, "bkey");
await bus.BindQueueAsync(dlq, dlx, dlq);

// ---------------------------------------------------------------------------
// CancellationToken principal — Ctrl+C ou SIGTERM encerram tudo
// ---------------------------------------------------------------------------
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// ---------------------------------------------------------------------------
// Contadores
// ---------------------------------------------------------------------------
long consumed = 0;
long errors = 0;
long publishedOk = 0;
long publishedError = 0;

// ---------------------------------------------------------------------------
// Consumidor — 1 channel dedicado, fica vivo até Ctrl+C
// ---------------------------------------------------------------------------
_ = bus.ConsumeAsync<BombardierMessage>(queue, async (msg, _) =>
{
    try
    {
        var total = Interlocked.Increment(ref consumed);
        if (total % batchReport == 0)
            logger.LogInformation("[consumer] {Count} msgs processadas | published: {Pub}",
                total, Interlocked.Read(ref publishedOk));
        return AckStrategy.Ack;
    }
    catch (Exception ex)
    {
        Interlocked.Increment(ref errors);
        logger.LogError(ex, "Erro ao processar mensagem {Id}", msg.Id);
        return AckStrategy.NackDeadLetter;
    }
}, prefetch: (ushort)prefetch, ct: cts.Token);

// ---------------------------------------------------------------------------
// Relatório periódico de saúde — logado a cada 10s independente do modo
// ---------------------------------------------------------------------------
_ = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(10_000, cts.Token); }
        catch (OperationCanceledException) { break; }

        logger.LogInformation(
            "[heartbeat] connection: aberta | published: {Pub} | consumed: {Con} | errors: {Err}",
            Interlocked.Read(ref publishedOk),
            Interlocked.Read(ref consumed),
            Interlocked.Read(ref errors));
    }
});

// ---------------------------------------------------------------------------
// MODO SERVER — simula API: publica bursts periódicos, nunca encerra sozinho
// ---------------------------------------------------------------------------
if (mode == "server")
{
    logger.LogInformation("Modo servidor ativo. Connection permanece aberta. Ctrl+C para encerrar.");
    logger.LogInformation("Acompanhe os channels em: http://localhost:15672");

    int msgId = 0;
    while (!cts.Token.IsCancellationRequested)
    {
        // Simula N requisições chegando na API ao mesmo tempo
        var burstTasks = Enumerable.Range(0, serverBatch).Select(_ => Task.Run(async () =>
        {
            int id = Interlocked.Increment(ref msgId);
            try
            {
                await bus.PublishAsync(exchange, "bkey", new BombardierMessage
                {
                    Id = id,
                    Payload = $"server-{id}",
                    SentAt = DateTime.UtcNow,
                }, ct: cts.Token);
                Interlocked.Increment(ref publishedOk);
            }
            catch (OperationCanceledException) { /* encerramento gracioso */ }
            catch (Exception ex)
            {
                Interlocked.Increment(ref publishedError);
                logger.LogWarning("[server] Falha ao publicar {Id}: {Msg}", id, ex.Message);
            }
        }));

        await Task.WhenAll(burstTasks);

        try { await Task.Delay(serverInterval, cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}

// ---------------------------------------------------------------------------
// MODO STRESS — bombardeio com N workers e estatísticas no final
// ---------------------------------------------------------------------------
else
{
    logger.LogInformation("--- Smoke test (10 msgs com confirm) ---");
    for (int i = 0; i < 10; i++)
    {
        await bus.PublishWithConfirmAsync(exchange, "bkey", new BombardierMessage
        {
            Id = i,
            Payload = $"smoke-{i}",
            SentAt = DateTime.UtcNow,
        });
    }
    logger.LogInformation("Smoke test concluido");

    logger.LogInformation("--- Stress test: {Total} msgs x {Workers} workers ---",
        stressTotal, stressParallel);

    var publishSw = Stopwatch.StartNew();

    var publishTasks = Enumerable.Range(0, stressParallel).Select(worker => Task.Run(async () =>
    {
        for (int i = worker; i < stressTotal; i += stressParallel)
        {
            if (cts.Token.IsCancellationRequested) break;
            try
            {
                await bus.PublishAsync(exchange, "bkey", new BombardierMessage
                {
                    Id = i,
                    Payload = $"bulk-{i}",
                    SentAt = DateTime.UtcNow,
                }, ct: cts.Token);
                Interlocked.Increment(ref publishedOk);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref publishedError);
                logger.LogWarning("Falha ao publicar {Id}: {Msg}", i, ex.Message);
            }
        }
    }));

    using var progressCts = new CancellationTokenSource();
    var progressTask = Task.Run(async () =>
    {
        while (!progressCts.Token.IsCancellationRequested)
        {
            try { await Task.Delay(3_000, progressCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            var ok = Interlocked.Read(ref publishedOk);
            var err = Interlocked.Read(ref publishedError);
            logger.LogInformation("[publish] {Ok}/{Total} ({Pct:F1}%) | erros: {Err} | rate: {Rate}/s",
                ok, stressTotal, ok * 100.0 / stressTotal, err,
                (int)(ok / publishSw.Elapsed.TotalSeconds));
        }
    });

    await Task.WhenAll(publishTasks);
    progressCts.Cancel();
    await progressTask;
    publishSw.Stop();

    logger.LogInformation("Publish concluido: {Ok} ok | {Err} erros | {Ms} ms | {Rate}/s",
        publishedOk, publishedError,
        publishSw.ElapsedMilliseconds,
        (int)(publishedOk / publishSw.Elapsed.TotalSeconds));

    // Aguarda consumo total
    using var waitCts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    var waitSw = Stopwatch.StartNew();
    long expected = 10 + stressTotal;

    try
    {
        while (!waitCts.Token.IsCancellationRequested)
        {
            await Task.Delay(5_000, waitCts.Token);
            var c = Interlocked.Read(ref consumed);
            var e = Interlocked.Read(ref errors);
            logger.LogInformation("[status] {C}/{Exp} ({Pct:F1}%) | erros: {E} | tempo: {T:mm\\:ss}",
                c, expected, c * 100.0 / expected, e, waitSw.Elapsed);
            if (c + e >= expected) { logger.LogInformation("Consumo concluido."); break; }
        }
    }
    catch (OperationCanceledException) { }

    waitSw.Stop();
    logger.LogInformation("=== Estatisticas Finais ===");
    logger.LogInformation("  Publicadas  : {V}", publishedOk + 10);
    logger.LogInformation("  Consumidas  : {V}", Interlocked.Read(ref consumed));
    logger.LogInformation("  Erros       : {V}", Interlocked.Read(ref errors));
    logger.LogInformation("  Throughput  : {Rate}/s",
        (int)(Interlocked.Read(ref consumed) / Math.Max(waitSw.Elapsed.TotalSeconds, 1)));
    logger.LogInformation("  Tempo total : {T:mm\\:ss}", waitSw.Elapsed);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
static int Env(string key, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : fallback;

// ---------------------------------------------------------------------------
// Modelo de mensagem
// ---------------------------------------------------------------------------
/// <summary>Mensagem usada no bombardeio e no modo servidor.</summary>
public sealed class BombardierMessage
{
    public int Id { get; set; }
    public string Payload { get; set; } = string.Empty;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}


// ---------------------------------------------------------------------------
// Configuração de logging
