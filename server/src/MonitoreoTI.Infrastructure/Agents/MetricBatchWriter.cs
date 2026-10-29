using System.Data;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Agents;

public sealed class MetricBatchOptions
{
    public int QueueCapacity { get; set; } = 50_000;
    public int MaxBatchSize { get; set; } = 1_000;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Guarda las métricas por lotes (una carga masiva por empresa cada pocos segundos) en vez de una escritura
/// por mensaje. La confirmación (ack) al agente ocurre al encolar, no al guardar: ante una caída del servidor
/// se pierde, como mucho, lo que había en memoria (segundos de métricas, que se vuelven a generar cada minuto).
/// </summary>
public sealed class MetricBatchWriter : BackgroundService, IMetricSink
{
    private const int MaxAttempts = 3;

    private readonly Channel<MetricsSample> _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly MetricBatchOptions _options;
    private readonly ILogger<MetricBatchWriter> _logger;

    public MetricBatchWriter(IServiceScopeFactory scopes, MetricBatchOptions options, ILogger<MetricBatchWriter> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
        _queue = Channel.CreateBounded<MetricsSample>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
        });
    }

    public bool TryEnqueue(MetricsSample sample) => _queue.Writer.TryWrite(sample);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // Deja que se acumule un poco para escribir en lote y no muestra por muestra.
                await Task.Delay(_options.FlushInterval, stoppingToken);
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Apagado: se vacía lo pendiente abajo.
        }

        await FlushAsync(CancellationToken.None);
    }

    /// <summary>Guarda todo lo encolado. Público para las pruebas.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = new List<MetricsSample>();
        while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var sample))
            batch.Add(sample);

        foreach (var company in batch.GroupBy(s => s.Identity.CompanyId))
            await WriteWithRetryAsync(company.Key, company.ToList(), cancellationToken);

        // Quedó más en cola que un lote: se sigue sin esperar.
        if (!cancellationToken.IsCancellationRequested && _queue.Reader.Count > 0)
            await FlushAsync(cancellationToken);
    }

    private async Task WriteWithRetryAsync(int companyId, List<MetricsSample> samples, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteAsync(companyId, samples, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(ex, "Se descartaron {Count} métricas de la empresa {CompanyId} tras {Attempts} intentos.",
                        samples.Count, companyId, attempt);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private async Task WriteAsync(int companyId, List<MetricsSample> samples, CancellationToken cancellationToken)
    {
        // Una muestra repetida (el agente reenvía lo que no alcanzó a confirmarse) se cuenta una sola vez.
        var unique = samples
            .GroupBy(s => (s.Identity.MachineId, s.TimestampUtc.Ticks / TimeSpan.TicksPerSecond))
            .Select(g => g.Last())
            .ToList();

        var table = new DataTable();
        table.Columns.Add("maquina_id", typeof(int));
        table.Columns.Add("fecha_hora", typeof(DateTime));
        table.Columns.Add("cpu", typeof(decimal));
        table.Columns.Add("ram", typeof(decimal));
        table.Columns.Add("disco", typeof(decimal));
        table.Columns.Add("temperatura", typeof(decimal));
        foreach (var s in unique)
            table.Rows.Add(s.Identity.MachineId, s.TimestampUtc, ToDb(s.Cpu), ToDb(s.Ram), ToDb(s.Disk), ToDb(s.Temperature));

        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        // La conexión de EF ya trae el contexto de empresa (Row-Level Security) fijado al abrirse.
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var create = new SqlCommand("""
            CREATE TABLE #lote_metricas (
                maquina_id INT NOT NULL, fecha_hora DATETIME2(0) NOT NULL,
                cpu DECIMAL(5,2) NULL, ram DECIMAL(5,2) NULL, disco DECIMAL(5,2) NULL, temperatura DECIMAL(5,2) NULL);
            """, connection, transaction))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#lote_metricas" })
        {
            await bulk.WriteToServerAsync(table, cancellationToken);
        }

        await using (var insert = new SqlCommand("""
            INSERT INTO dbo.metricas (empresa_id, maquina_id, fecha_hora, cpu, ram, disco, temperatura)
            SELECT @empresa, l.maquina_id, l.fecha_hora, l.cpu, l.ram, l.disco, l.temperatura
            FROM #lote_metricas l
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.metricas m
                WHERE m.empresa_id = @empresa AND m.maquina_id = l.maquina_id AND m.fecha_hora = l.fecha_hora);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@empresa", companyId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static object ToDb(decimal? value) => value.HasValue ? value.Value : DBNull.Value;
}
