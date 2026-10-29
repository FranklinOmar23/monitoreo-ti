using System.Collections.Concurrent;

namespace MonitoreoTI.Application.Agents;

/// <summary>
/// Último contacto de cada agente conectado, en memoria. Un heartbeat solo actualiza este mapa;
/// el servicio de presencia lo vuelca a la base en lote, en vez de una escritura por mensaje.
/// </summary>
public sealed class AgentPresenceTracker(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, Entry> _agents = new();

    private sealed record Entry(AgentIdentity Identity, DateTime LastSeenUtc, bool Dirty);

    public void Connected(AgentIdentity identity) =>
        _agents[identity.AgentId] = new Entry(identity, _clock.GetUtcNow().UtcDateTime, Dirty: true);

    /// <summary>Registra contacto. Si el agente ya se desconectó (carrera con el cierre) no hace nada.</summary>
    public void Seen(int agentId)
    {
        while (_agents.TryGetValue(agentId, out var current))
        {
            var updated = current with { LastSeenUtc = _clock.GetUtcNow().UtcDateTime, Dirty = true };
            if (_agents.TryUpdate(agentId, updated, current))
                return;
        }
    }

    public void Disconnected(int agentId) => _agents.TryRemove(agentId, out _);

    public int ConnectedCount => _agents.Count;

    /// <summary>Entradas con contacto nuevo desde la última llamada (y las marca como guardadas).</summary>
    public IReadOnlyCollection<AgentPresenceEntry> TakeDirty()
    {
        var result = new List<AgentPresenceEntry>();
        foreach (var (agentId, entry) in _agents)
        {
            if (!entry.Dirty)
                continue;

            // Si llegó un heartbeat entre la lectura y la marca, queda sucio y se guarda en el siguiente ciclo.
            if (_agents.TryUpdate(agentId, entry with { Dirty = false }, entry))
                result.Add(new AgentPresenceEntry(entry.Identity, entry.LastSeenUtc));
        }
        return result;
    }
}
