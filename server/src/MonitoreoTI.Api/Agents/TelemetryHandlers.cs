using System.Text.Json;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Api.Agents;

/// <summary>
/// Base de los manejadores: lee la carga útil del mensaje. Un mensaje mal formado es del agente, no un fallo
/// del servidor: se descarta con aviso (y se confirma) en vez de bloquear la cola del agente con un reintento eterno.
/// </summary>
public abstract class TelemetryHandler<TData>(ILogger logger) : IAgentMessageHandler where TData : class
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public abstract string Tipo { get; }

    public async Task HandleAsync(AgentIdentity identity, AgentEnvelope message, CancellationToken cancellationToken)
    {
        TData? data;
        try
        {
            data = message.Datos.Deserialize<TData>(JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Agente {AgentId}: mensaje {Tipo} con formato inválido; se descarta.", identity.AgentId, Tipo);
            return;
        }

        if (data is null)
        {
            logger.LogWarning("Agente {AgentId}: mensaje {Tipo} vacío; se descarta.", identity.AgentId, Tipo);
            return;
        }

        await HandleAsync(identity, message, data, cancellationToken);
    }

    protected abstract Task HandleAsync(AgentIdentity identity, AgentEnvelope message, TData data, CancellationToken cancellationToken);
}

public sealed class MetricsHandler(IMetricSink sink, IRealtimeNotifier notifier, TimeProvider clock, ILogger<MetricsHandler> logger)
    : TelemetryHandler<MetricasData>(logger)
{
    public override string Tipo => "metricas";

    protected override async Task HandleAsync(AgentIdentity identity, AgentEnvelope message, MetricasData data, CancellationToken cancellationToken)
    {
        var sample = new MetricsSample(
            identity,
            TelemetryRules.SampleTime(message.Ts, clock.GetUtcNow().UtcDateTime),
            TelemetryRules.Percent(data.Cpu),
            TelemetryRules.Percent(data.Ram),
            TelemetryRules.Percent(data.Disco),
            TelemetryRules.Celsius(data.Temperatura));

        // Cola llena: sin ack el agente conserva el mensaje y reintenta cuando el servidor se descongestione.
        if (!sink.TryEnqueue(sample))
            throw new InvalidOperationException("La cola de métricas está llena.");

        // A quien tenga abierta la ficha de este equipo (grupo de la máquina en SignalR).
        await notifier.MetricsReceivedAsync(identity.CompanyId, identity.MachineId,
            new MetricPoint(sample.TimestampUtc, sample.Cpu, sample.Ram, sample.Disk, sample.Temperature), cancellationToken);

    }
}

public sealed class HardwareInventoryHandler(IAgentInventory inventory, ILogger<HardwareInventoryHandler> logger)
    : TelemetryHandler<InventarioHardwareData>(logger)
{
    public override string Tipo => "inventario_hardware";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, InventarioHardwareData data, CancellationToken cancellationToken) =>
        inventory.SaveHardwareAsync(identity, data, cancellationToken);
}

public sealed class SoftwareInventoryHandler(IAgentInventory inventory, ILogger<SoftwareInventoryHandler> logger)
    : TelemetryHandler<InventarioSoftwareData>(logger)
{
    public override string Tipo => "inventario_software";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, InventarioSoftwareData data, CancellationToken cancellationToken) =>
        inventory.SaveSoftwareAsync(identity, data, cancellationToken);
}

public sealed class NetworkInterfacesHandler(IAgentInventory inventory, ILogger<NetworkInterfacesHandler> logger)
    : TelemetryHandler<InterfacesRedData>(logger)
{
    public override string Tipo => "interfaces_red";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, InterfacesRedData data, CancellationToken cancellationToken) =>
        inventory.SaveInterfacesAsync(identity, data, cancellationToken);
}

// ---------------------------------------------------------------- Fase 3: salud del equipo

public sealed class VolumesHandler(IAgentHealthStore store, ILogger<VolumesHandler> logger) : TelemetryHandler<VolumenesData>(logger)
{
    public override string Tipo => "volumenes";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, VolumenesData data, CancellationToken cancellationToken) =>
        store.SaveVolumesAsync(identity, data, cancellationToken);
}

public sealed class ProcessesHandler(IAgentHealthStore store, ILogger<ProcessesHandler> logger) : TelemetryHandler<ProcesosData>(logger)
{
    public override string Tipo => "procesos";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, ProcesosData data, CancellationToken cancellationToken) =>
        store.SaveProcessesAsync(identity, data, cancellationToken);
}

public sealed class ServicesHandler(IAgentHealthStore store, ILogger<ServicesHandler> logger) : TelemetryHandler<ServiciosData>(logger)
{
    public override string Tipo => "servicios";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, ServiciosData data, CancellationToken cancellationToken) =>
        store.SaveServicesAsync(identity, data, cancellationToken);
}

public sealed class EventsHandler(IAgentHealthStore store, ILogger<EventsHandler> logger) : TelemetryHandler<EventosData>(logger)
{
    public override string Tipo => "eventos";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, EventosData data, CancellationToken cancellationToken) =>
        store.SaveEventsAsync(identity, data, cancellationToken);
}

public sealed class DiskHealthHandler(IAgentHealthStore store, ILogger<DiskHealthHandler> logger) : TelemetryHandler<SaludDiscosData>(logger)
{
    public override string Tipo => "salud_discos";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, SaludDiscosData data, CancellationToken cancellationToken) =>
        store.SaveDiskHealthAsync(identity, data, cancellationToken);
}

public sealed class SecurityHandler(IAgentHealthStore store, ILogger<SecurityHandler> logger) : TelemetryHandler<SeguridadData>(logger)
{
    public override string Tipo => "seguridad";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, SeguridadData data, CancellationToken cancellationToken) =>
        store.SaveSecurityAsync(identity, data, cancellationToken);
}

// ---------------------------------------------------------------- Fase 4: recolector SNMP

/// <summary>Resultado de un sondeo SNMP (sección 11.1): solo de un agente recolector y de un dispositivo que tenga asignado.</summary>
public sealed class CommandResultHandler(MonitoreoTI.Infrastructure.Remote.AgentCommands commands, ILogger<CommandResultHandler> logger)
    : TelemetryHandler<MonitoreoTI.Application.Remote.CommandResultData>(logger)
{
    public override string Tipo => "resultado_comando";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, MonitoreoTI.Application.Remote.CommandResultData data, CancellationToken cancellationToken) =>
        commands.SaveResultAsync(identity, data, cancellationToken);
}

public sealed class SessionActivityHandler(MonitoreoTI.Infrastructure.Remote.RemoteSessionLog log, ILogger<SessionActivityHandler> logger)
    : TelemetryHandler<MonitoreoTI.Application.Remote.SessionActivityData>(logger)
{
    public override string Tipo => "actividad_sesion";

    protected override async Task HandleAsync(AgentIdentity identity, AgentEnvelope message, MonitoreoTI.Application.Remote.SessionActivityData data, CancellationToken cancellationToken)
    {
        if (!await log.RecordActivityAsync(identity, data, cancellationToken))
            logger.LogWarning("Agente {AgentId}: actividad de una sesión que no es suya o no es de archivos; se descarta.", identity.AgentId);
    }
}

public sealed class SnmpResultHandler(ISnmpCollectors collectors, TimeProvider clock, ILogger<SnmpResultHandler> logger)
    : TelemetryHandler<SnmpResultadoData>(logger)
{
    public override string Tipo => "snmp_resultado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, SnmpResultadoData data, CancellationToken cancellationToken) =>
        collectors.SaveResultAsync(identity, data, TelemetryRules.SampleTime(message.Ts, clock.GetUtcNow().UtcDateTime), cancellationToken);
}
