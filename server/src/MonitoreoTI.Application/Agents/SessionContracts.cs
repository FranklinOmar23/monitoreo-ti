using System.Text.Json;

namespace MonitoreoTI.Application.Agents;

/// <summary>Agente autenticado: la empresa y la máquina salen de la base, nunca del mensaje.</summary>
public sealed record AgentIdentity(int CompanyId, int AgentId, int MachineId, bool IsCollector);

/// <summary>Sobre de mensajes del protocolo (sección 9). Los nombres de campo son parte del contrato.</summary>
public sealed record AgentEnvelope(int V, string Tipo, string Id, DateTimeOffset Ts, JsonElement Datos);

/// <summary>Datos del mensaje "hola" que abre cada conexión (nombres de campo del protocolo, sección 9).</summary>
public sealed record HolaData(
    string Version,
    string? Hostname,
    string? So,
    [property: System.Text.Json.Serialization.JsonPropertyName("es_recolector")] bool EsRecolector);

/// <summary>Persistencia del estado de conexión de los agentes. Cada llamada usa su propio ámbito de empresa.</summary>
public interface IAgentSessions
{
    /// <summary>Valida la credencial permanente. null si no existe, fue revocada o la máquina está de baja.</summary>
    Task<AgentIdentity?> AuthenticateAsync(string? credential, CancellationToken cancellationToken);

    /// <summary>El agente abrió una conexión: registra versión y contacto, y saca la máquina de "desconectado".</summary>
    Task OnConnectedAsync(AgentIdentity identity, HolaData hola, CancellationToken cancellationToken);

    /// <summary>Guarda en lote el último contacto de los agentes (los heartbeat solo tocan memoria).</summary>
    Task FlushLastSeenAsync(IReadOnlyCollection<AgentPresenceEntry> entries, CancellationToken cancellationToken);

    /// <summary>Marca "desconectado" las máquinas cuyo agente no contacta desde antes de <paramref name="threshold"/>.</summary>
    Task<int> MarkStaleDisconnectedAsync(int companyId, DateTime threshold, CancellationToken cancellationToken);

    /// <summary>Guarda (o reescribe) la foto del semáforo de la empresa para ese día.</summary>
    Task SnapshotDailyStatusAsync(int companyId, DateOnly date, CancellationToken cancellationToken);

    Task<IReadOnlyList<int>> ListCompanyIdsAsync(CancellationToken cancellationToken);
}

public sealed record AgentPresenceEntry(AgentIdentity Identity, DateTime LastSeenUtc);
