using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Recolectores SNMP (secciones 11.1 y 11.3). Singleton: cada operación abre su ámbito con la empresa del agente.
/// Al recibir un sondeo actualiza el dispositivo y sus puertos, guarda los contadores, la tabla MAC y los vecinos LLDP,
/// marca troncales, cruza cada MAC con las tarjetas de red de los equipos con agente y registra los equipos movidos.
/// Un agente solo puede reportar los dispositivos que tiene asignados.
/// </summary>
public sealed class SnmpCollectors(
    IServiceScopeFactory scopes,
    SecretProtector protector,
    IRealtimeNotifier notifier,
    OuiDirectory oui,
    IConfiguration configuration,
    ILogger<SnmpCollectors> logger) : ISnmpCollectors
{
    /// <summary>Cuánto vale una MAC vista en un puerto para decir "aquí estaba este equipo".</summary>
    private static readonly TimeSpan KnownWindow = TimeSpan.FromHours(24);

    /// <summary>Las MAC que no se ven en este tiempo se borran de la tabla.</summary>
    private static readonly TimeSpan MacRetention = TimeSpan.FromDays(7);

    private int IntervalSeconds => Math.Clamp(int.TryParse(configuration["Red:IntervaloSnmpSegundos"], out var s) ? s : 300, 60, 3600);

    public async Task<CollectorConfig?> ConfigAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);

        var hash = await db.Agents.Where(a => a.Id == identity.AgentId).Select(a => a.TokenHash).FirstOrDefaultAsync(cancellationToken);
        if (hash is null)
            return null;
        var devices = await db.NetworkDevices.AsNoTracking()
            .Where(d => d.CollectorAgentId == identity.AgentId && d.Status != NetworkDeviceStatus.Retired
                && d.ManagementIp != null && d.SnmpUser != null && d.SnmpEncryptedCredential != null)
            .OrderBy(d => d.Name)
            .ToListAsync(cancellationToken);

        var list = new List<CollectorDevice>();
        foreach (var d in devices)
        {
            // Las claves se abren solo aquí, en memoria, para sellarlas otra vez con la llave de ese agente.
            var secret = protector.Unprotect(Encoding.UTF8.GetString(d.SnmpEncryptedCredential!));
            if (secret is null)
            {
                logger.LogWarning("Dispositivo {DeviceId}: no se pudieron abrir sus claves SNMP; no se consulta.", d.Id);
                continue;
            }
            list.Add(new CollectorDevice(d.Id, d.Name, d.ManagementIp!, Text(d.Type), d.SnmpUser!, SnmpCredentialSeal.Seal(hash, secret),
                Api: SealApi(d, hash)));
        }

        // Impresoras (sin agente): v3 o v2c según la impresora; solo se leen sus consumibles.
        var printers = await db.Machines.AsNoTracking()
            .Where(m => m.CollectorAgentId == identity.AgentId && m.Status != MachineStatus.Retired
                && m.IpAddress != null && m.SnmpVersion != null && m.SnmpEncryptedCredential != null)
            .OrderBy(m => m.Name)
            .ToListAsync(cancellationToken);
        foreach (var m in printers)
        {
            var secret = protector.Unprotect(Encoding.UTF8.GetString(m.SnmpEncryptedCredential!));
            if (secret is null)
            {
                logger.LogWarning("Impresora {MachineId}: no se pudieron abrir sus datos SNMP; no se consulta.", m.Id);
                continue;
            }
            list.Add(new CollectorDevice(m.Id, m.Name, m.IpAddress!, "impresora", m.SnmpUser ?? "", SnmpCredentialSeal.Seal(hash, secret),
                m.SnmpVersion!, "impresora"));
        }
        return list.Count == 0 ? null : new CollectorConfig(IntervalSeconds, list);
    }

    /// <summary>El token de la API, sellado para el recolector; null si no tiene o no se pudo abrir.</summary>
    private CollectorApi? SealApi(NetworkDevice d, string agentTokenHash)
    {
        if (d.ApiUrl is null || d.ApiEncryptedToken is null)
            return null;
        var token = protector.Unprotect(Encoding.UTF8.GetString(d.ApiEncryptedToken));
        if (token is null)
        {
            logger.LogWarning("Dispositivo {DeviceId}: no se pudo abrir el token de su API; se consulta solo por SNMP.", d.Id);
            return null;
        }
        return new CollectorApi(d.ApiUrl, SnmpCredentialSeal.Seal(agentTokenHash, JsonSerializer.Serialize(new { token })), d.ApiAcceptSelfSigned);
    }

    public async Task SaveResultAsync(AgentIdentity identity, SnmpResultadoData data, DateTime sampledAtUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        if (data.MaquinaId is int printerId)
        {
            await SavePrinterAsync(db, identity, printerId, data, sampledAtUtc, cancellationToken);
            return;
        }

        var device = await db.NetworkDevices.Include(d => d.Ports)
            .FirstOrDefaultAsync(d => d.Id == data.DispositivoId && d.CollectorAgentId == identity.AgentId
                && d.Status != NetworkDeviceStatus.Retired, cancellationToken);
        if (device is null)
        {
            logger.LogWarning("Agente {AgentId}: reportó el dispositivo {DeviceId}, que no tiene asignado; se ignora.", identity.AgentId, data.DispositivoId);
            return;
        }
        // Un resultado viejo que esperaba en la cola del agente no pisa uno más nuevo.
        if (device.LastPolledAt is DateTime last && sampledAtUtc <= last)
            return;

        var previousStatus = device.Status;
        if (!data.Ok)
        {
            var previousError = device.LastError;
            device.LastError = Cut(data.Error ?? "El dispositivo no respondió por SNMP.", 300);
            device.Status = NetworkDeviceStatus.NotResponding;
            await db.SaveChangesAsync(cancellationToken);
            // Un equipo recién registrado ya está "sin respuesta": el panel se entera por el error nuevo, no por el estado.
            if (previousStatus != device.Status || previousError != device.LastError)
                await notifier.PortStatusChangedAsync(identity.CompanyId, device.BranchId, device.Id, null, Text(device.Status), cancellationToken);
            return;
        }

        device.LastError = null;
        device.LastPolledAt = sampledAtUtc;
        // El motor de alertas lo pasa a advertencia o crítico si tiene problemas abiertos.
        if (device.Status is NetworkDeviceStatus.NotResponding)
            device.Status = NetworkDeviceStatus.Online;
        device.Firmware ??= SnmpRules.FirmwareFrom(data.Sistema?.Descripcion);

        // Regla de la sucursal si la tiene; si no, la de la empresa; si no, la sección 14.
        var ruleRows = await db.AlertRules.AsNoTracking().Where(r => r.BranchId == null || r.BranchId == device.BranchId).ToListAsync(cancellationToken);
        var thresholds = new Dictionary<string, RuleThreshold>();
        foreach (var r in ruleRows.Where(r => r.BranchId == null).Concat(ruleRows.Where(r => r.BranchId != null)))
            thresholds[EnumMemberConverter<AlertMetric>.ToProvider(r.Metric)] = new RuleThreshold(r.WarningThreshold, r.CriticalThreshold, r.DurationMinutes, r.IsActive);
        var rules = new RuleSet(thresholds);
        var errorWarning = rules.For("puerto_errores")?.Warning;

        var mapping = SnmpRules.MapToPortNumbers(data.Interfaces ?? [], device.Ports.Select(p => p.Number));
        var portByIndex = mapping.ToDictionary(m => m.Value.Indice, m => device.Ports.First(p => p.Number == m.Key));
        var portIds = device.Ports.Select(p => p.Id).ToList();

        var lastMetrics = await db.PortMetrics.AsNoTracking()
            .Where(m => portIds.Contains(m.PortId))
            .GroupBy(m => m.PortId)
            .Select(g => g.OrderByDescending(m => m.Timestamp).First())
            .ToDictionaryAsync(m => m.PortId, cancellationToken);
        var knownSince = sampledAtUtc - KnownWindow;
        // Un puerto asignado a mano tiene su equipo aunque SNMP no lo vea desde hace días.
        var manualPorts = device.Ports.Where(p => p.IsManualAssignment).Select(p => p.Id).ToList();
        var knownPorts = await db.MacTable.AsNoTracking()
            .Where(m => portIds.Contains(m.PortId) && m.MachineId != null && (m.LastSeenAt >= knownSince || manualPorts.Contains(m.PortId)))
            .Select(m => m.PortId).Distinct().ToListAsync(cancellationToken);

        // Vecinos LLDP que son equipos de red registrados: su puerto local es troncal y el enlace queda registrado.
        var neighbors = await ResolveNeighborsAsync(db, device, data.Lldp ?? [], portByIndex, cancellationToken);
        var macsByPort = (data.Mac ?? []).Where(m => portByIndex.ContainsKey(m.IfIndex))
            .GroupBy(m => portByIndex[m.IfIndex].Id).ToDictionary(g => g.Key, g => g.ToList());

        var changes = new List<(Port Port, string Status)>();
        foreach (var (number, iface) in mapping)
        {
            var port = device.Ports.First(p => p.Number == number);
            var errors = Sum(iface.ErroresEntrada, iface.ErroresSalida);
            var previous = lastMetrics.GetValueOrDefault(port.Id);
            var rate = SnmpRules.ErrorsPerHour(previous is null ? null : Sum(previous.ErrorsIn, previous.ErrorsOut), previous?.Timestamp, errors, sampledAtUtc);
            db.PortMetrics.Add(new PortMetric
            {
                PortId = port.Id,
                Timestamp = sampledAtUtc,
                BytesIn = iface.EntradaBytes,
                BytesOut = iface.SalidaBytes,
                ErrorsIn = iface.ErroresEntrada,
                ErrorsOut = iface.ErroresSalida,
                Discards = iface.Descartes,
            });

            if (!string.IsNullOrWhiteSpace(iface.Nombre))
                port.Name = Cut(iface.Nombre, 50);
            if (iface.Operativo == "up" && iface.VelocidadMbps is long speed and > 0)
                port.SpeedMbps = (int)Math.Min(speed, int.MaxValue);
            if (!port.IsManualAssignment)
                port.IsTrunk = SnmpRules.IsTrunk(neighbors.ContainsKey(port.Id), macsByPort.GetValueOrDefault(port.Id)?.Count ?? 0, port.IsVmHost);

            var status = EnumMemberConverter<PortStatus>.FromProvider(SnmpRules.PortStatus(iface, rate, errorWarning, knownPorts.Contains(port.Id)));
            if (port.Status != status)
            {
                changes.Add((port, Text(status)));
                port.Status = status;
            }
        }

        await SaveLinksAsync(db, device, neighbors, sampledAtUtc, cancellationToken);
        var moved = await SaveMacTableAsync(db, device, macsByPort, data.Arp ?? [], sampledAtUtc, cancellationToken);
        // La ARP del equipo (sobre todo la del firewall, que es la puerta de enlace de todas las VLAN) alimenta el
        // inventario de direcciones IP de las subredes registradas (decisión 72).
        // Con las interfaces del mismo sondeo se sabe en qué VLAN o interfaz está cada IP.
        var ifNames = (data.Interfaces ?? []).GroupBy(f => f.Indice).ToDictionary(g => g.Key, g => g.First().Nombre ?? g.First().Alias);
        await IpInventory.RecordAsync(db, oui, IpInventory.FromArp(data.Arp ?? [], index => ifNames.GetValueOrDefault(index)), null, sampledAtUtc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (machine, from, to) in moved)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "equipo.movido",
                Entity = "dispositivos_red",
                EntityId = device.Id.ToString(),
                Details = JsonSerializer.Serialize(new { antes = new { nombre = machine, puerto = from }, despues = new { nombre = machine, puerto = to } }),
            });
        }
        foreach (var (port, status) in changes.Where(c => c.Status is "caido" || c.Port.Status == PortStatus.Down))
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "puerto.estado",
                Entity = "puertos",
                EntityId = port.Id.ToString(),
                Details = JsonSerializer.Serialize(new { antes = (object?)null, despues = new { nombre = $"{device.Name} {port.Name ?? port.Number.ToString()}", estado = status } }),
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (port, status) in changes)
            await notifier.PortStatusChangedAsync(identity.CompanyId, device.BranchId, device.Id, port.Id, status, cancellationToken);
        if (previousStatus != device.Status)
            await notifier.PortStatusChangedAsync(identity.CompanyId, device.BranchId, device.Id, null, Text(device.Status), cancellationToken);
    }

    /// <summary>
    /// Consumibles de una impresora: reemplazan sus componentes "toner" con el porcentaje restante (la regla de tóner
    /// del motor de alertas los evalúa). El sondeo bueno cuenta como reporte del equipo.
    /// </summary>
    private async Task SavePrinterAsync(MonitoreoDbContext db, AgentIdentity identity, int machineId, SnmpResultadoData data, DateTime sampledAt, CancellationToken ct)
    {
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId && m.CollectorAgentId == identity.AgentId
            && m.Status != MachineStatus.Retired, ct);
        if (machine is null)
        {
            logger.LogWarning("Agente {AgentId}: reportó la impresora {MachineId}, que no tiene asignada; se ignora.", identity.AgentId, machineId);
            return;
        }
        if (machine.LastSnmpPollAt is DateTime last && sampledAt <= last)
            return;

        if (!data.Ok)
        {
            machine.LastSnmpError = Cut(data.Error ?? "La impresora no respondió por SNMP.", 300);
            await db.SaveChangesAsync(ct);
            return;
        }

        machine.LastSnmpError = null;
        machine.LastSnmpPollAt = sampledAt;
        machine.LastReportAt = sampledAt;
        var cameBack = machine.Status == MachineStatus.Disconnected;
        if (cameBack)
            machine.Status = MachineStatus.Healthy;

        var old = await db.Components.Where(c => c.MachineId == machineId && c.Type == "toner").ToListAsync(ct);
        db.Components.RemoveRange(old);
        foreach (var supply in data.Consumibles ?? [])
        {
            var percent = PrinterSupplies.Percent(supply.Nivel, supply.Maximo);
            var waste = PrinterSupplies.IsWasteReceptacle(supply.Clase);
            db.Components.Add(new Domain.Entities.Inventory.Component
            {
                MachineId = machineId,
                Type = "toner",
                Description = Cut(supply.Descripcion ?? $"Consumible {supply.Indice}", 200),
                // El depósito de residuos se llena: su porcentaje no es "lo que queda" y no dispara la regla de tóner.
                Capacity = waste ? null : percent is int p ? $"{p} %" : null,
                // Para mostrar; las alertas usan la regla de tóner de la empresa.
                HealthStatus = waste || percent is null ? HealthStatus.Unknown
                    : percent < 5 ? HealthStatus.Critical : percent < 20 ? HealthStatus.Warning : HealthStatus.Healthy,
                Detail = waste ? (percent is int f ? $"Depósito de residuos · {f} % lleno" : "Depósito de residuos")
                    : percent is null ? supply.Nivel == -3 ? "Le queda (la impresora no informa cuánto)" : "Nivel no informado"
                    : $"{supply.Nivel} de {supply.Maximo}",
                UpdatedAt = sampledAt,
            });
        }
        await db.SaveChangesAsync(ct);
        if (cameBack)
            await notifier.MachineStatusChangedAsync(identity.CompanyId, machine.BranchId, machine.Id, Text(machine.Status), ct);
    }

    /// <summary>Puerto local → puerto del vecino (si el vecino es un dispositivo registrado y se reconoce su puerto).</summary>
    private static async Task<Dictionary<int, int?>> ResolveNeighborsAsync(
        MonitoreoDbContext db, NetworkDevice device, IReadOnlyList<SnmpVecinoData> lldp, Dictionary<int, Port> portByIndex, CancellationToken ct)
    {
        var result = new Dictionary<int, int?>();
        if (lldp.Count == 0)
            return result;

        var names = lldp.Where(n => n.Sistema is not null).Select(n => ShortName(n.Sistema!)).Distinct().ToList();
        var others = await db.NetworkDevices.AsNoTracking()
            .Where(d => d.Id != device.Id && d.Status != NetworkDeviceStatus.Retired && names.Contains(d.Name))
            .Select(d => new { d.Id, d.Name, Ports = d.Ports.Select(p => new { p.Id, p.Name }).ToList() })
            .ToListAsync(ct);

        foreach (var neighbor in lldp)
        {
            if (!portByIndex.TryGetValue(neighbor.PuertoLocal, out var local) || neighbor.Sistema is null)
                continue;
            var other = others.FirstOrDefault(o => string.Equals(o.Name, ShortName(neighbor.Sistema), StringComparison.OrdinalIgnoreCase));
            if (other is null)
                continue;
            var remote = other.Ports.FirstOrDefault(p => neighbor.Puerto is not null && SamePortName(p.Name, neighbor.Puerto));
            result[local.Id] = remote?.Id;
        }
        return result;
    }

    /// <summary>"sw-core-01.empresa.local" → "sw-core-01".</summary>
    private static string ShortName(string sysName) => sysName.Split('.')[0].Trim();

    /// <summary>LLDP suele dar el nombre largo ("TenGigabitEthernet1/1/1") y SNMP el corto ("Te1/1/1").</summary>
    private static bool SamePortName(string? ours, string theirs)
    {
        if (ours is null)
            return false;
        if (string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase))
            return true;
        static (string Prefix, string Numbers) Split(string name)
        {
            var i = name.IndexOfAny("0123456789".ToCharArray());
            return i < 0 ? (name, "") : (name[..i], name[i..]);
        }
        var (p1, n1) = Split(ours);
        var (p2, n2) = Split(theirs);
        return n1.Length > 0 && n1 == n2 && p1.Length >= 2 && p2.Length >= 2
            && string.Equals(p1[..2], p2[..2], StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SaveLinksAsync(MonitoreoDbContext db, NetworkDevice device, Dictionary<int, int?> neighbors, DateTime now, CancellationToken ct)
    {
        var portIds = device.Ports.Select(p => p.Id).ToList();
        var existing = await db.NetworkLinks.Where(l => l.Protocol == LinkProtocol.Lldp && portIds.Contains(l.SourcePortId)).ToListAsync(ct);
        foreach (var (local, remote) in neighbors)
        {
            if (remote is not int target)
                continue;
            var link = existing.FirstOrDefault(l => l.SourcePortId == local && l.TargetPortId == target);
            if (link is null)
                db.NetworkLinks.Add(new NetworkLink { SourcePortId = local, TargetPortId = target, Protocol = LinkProtocol.Lldp, DetectedAt = now });
            else
                link.DetectedAt = now;
        }
        // Lo que LLDP ya no ve se quita: el panel muestra solo los enlaces vigentes.
        db.NetworkLinks.RemoveRange(existing.Where(l => neighbors.GetValueOrDefault(l.SourcePortId) != l.TargetPortId));
    }

    /// <summary>
    /// Guarda las MAC de los puertos de acceso (las de las troncales no dicen dónde está un equipo) y las cruza con
    /// las tarjetas de red de los equipos con agente. Devuelve los equipos que aparecieron en otro puerto de acceso.
    /// </summary>
    private async Task<List<(string Machine, string From, string To)>> SaveMacTableAsync(
        MonitoreoDbContext db, NetworkDevice device, Dictionary<int, List<SnmpMacData>> macsByPort, IReadOnlyList<SnmpArpData> arp,
        DateTime now, CancellationToken ct)
    {
        var moved = new List<(string, string, string)>();
        var access = device.Ports.Where(p => !p.IsTrunk && macsByPort.ContainsKey(p.Id)).ToList();
        var seen = access.SelectMany(p => macsByPort[p.Id].Select(m => Normalize(m.Mac))).Where(m => m is not null).Cast<string>().Distinct().ToList();

        // Equipos con agente por sus tarjetas de red; equipos sin agente (impresoras) por la MAC de su ficha.
        var machines = await db.NetworkInterfaces.AsNoTracking().Where(i => seen.Contains(i.MacAddress))
            .Select(i => new { i.MacAddress, i.MachineId, i.IpAddress, Name = db.Machines.Where(m => m.Id == i.MachineId).Select(m => m.Name).First() })
            .ToListAsync(ct);
        var withoutAgent = await db.Machines.AsNoTracking()
            .Where(m => m.MacAddress != null && m.Status != MachineStatus.Retired && m.Agent == null)
            .Select(m => new { m.MacAddress, MachineId = m.Id, m.IpAddress, m.Name })
            .ToListAsync(ct);
        foreach (var m in withoutAgent)
            if (Normalize(m.MacAddress!) is string cardMac && seen.Contains(cardMac) && machines.All(x => x.MacAddress != cardMac))
                machines.Add(new { MacAddress = cardMac, m.MachineId, m.IpAddress, m.Name });
        var ipByMac = arp.Select(a => (Mac: Normalize(a.Mac), a.Ip)).Where(a => a.Mac is not null).GroupBy(a => a.Mac!).ToDictionary(g => g.Key, g => g.First().Ip);

        var portIds = device.Ports.Select(p => p.Id).ToList();
        var rows = await db.MacTable.Where(m => portIds.Contains(m.PortId)).ToListAsync(ct);
        var machineIds = machines.Select(m => m.MachineId).Distinct().ToList();
        var since = now - KnownWindow;
        var elsewhere = await db.MacTable.AsNoTracking()
            .Where(m => m.MachineId != null && machineIds.Contains(m.MachineId.Value) && !portIds.Contains(m.PortId) && m.LastSeenAt >= since)
            .Select(m => new { m.MachineId, m.PortId, Label = db.Ports.Where(p => p.Id == m.PortId).Select(p => p.Device!.Name + " " + (p.Name ?? p.Number.ToString())).First() })
            .ToListAsync(ct);

        foreach (var port in access)
        {
            foreach (var entry in macsByPort[port.Id])
            {
                if (Normalize(entry.Mac) is not string mac)
                    continue;
                var machine = machines.FirstOrDefault(m => m.MacAddress == mac);
                var row = rows.FirstOrDefault(r => r.PortId == port.Id && r.MacAddress == mac);
                if (row is null)
                {
                    row = new MacTableEntry { PortId = port.Id, MacAddress = mac, FirstSeenAt = now, Vendor = oui.Vendor(mac) };
                    db.MacTable.Add(row);
                    rows.Add(row);

                    var before = machine is null ? null
                        : rows.Where(r => r.MachineId == machine.MachineId && r.PortId != port.Id && r.LastSeenAt >= since)
                              .Select(r => device.Ports.First(p => p.Id == r.PortId)).Select(p => $"{device.Name} {p.Name ?? p.Number.ToString()}").FirstOrDefault()
                          ?? elsewhere.FirstOrDefault(e => e.MachineId == machine.MachineId)?.Label;
                    if (machine is not null && before is not null)
                        moved.Add((machine.Name, before, $"{device.Name} {port.Name ?? port.Number.ToString()}"));
                }
                row.LastSeenAt = now;
                row.Vlan = entry.Vlan ?? port.Vlan;
                if (machine is not null || !port.IsManualAssignment)
                    row.MachineId = machine?.MachineId;
                row.IpAddress = ipByMac.GetValueOrDefault(mac) ?? machine?.IpAddress ?? row.IpAddress;
            }
        }

        // Una troncal no ubica equipos: sus MAC se quitan; las viejas también, salvo la asignación manual de un puerto.
        var old = now - MacRetention;
        db.MacTable.RemoveRange(rows.Where(r =>
        {
            var port = device.Ports.First(p => p.Id == r.PortId);
            return port.IsTrunk || (!port.IsManualAssignment && r.LastSeenAt < old);
        }));
        return moved;
    }

    private static string? Normalize(string mac) => MacAddress.TryNormalize(mac, out var normalized) ? normalized : null;

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private static MonitoreoDbContext Open(AsyncServiceScope scope, int companyId)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        return scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
    }
}

/// <summary>
/// Fabricante de una MAC por su prefijo OUI (sección 11.1). Lee el registro del IEEE (oui.csv) si se indica en
/// Red:ArchivoOui; sin archivo, el fabricante queda vacío.
/// </summary>
public sealed class OuiDirectory
{
    private readonly Dictionary<string, string> _vendors = new(StringComparer.OrdinalIgnoreCase);

    public OuiDirectory(IConfiguration configuration, ILogger<OuiDirectory> logger)
    {
        var path = configuration["Red:ArchivoOui"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try
        {
            var table = CsvTable.Parse(File.ReadAllText(path));
            foreach (var row in table.Rows)
                if (row.Get("assignment") is { Length: 6 } prefix && row.Get("organization_name") is { } name)
                    _vendors[prefix] = name.Length > 150 ? name[..150] : name;
            logger.LogInformation("Tabla OUI cargada: {Count} fabricantes.", _vendors.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "No se pudo leer la tabla OUI {Path}.", path);
        }
    }

    public string? Vendor(string mac) => _vendors.GetValueOrDefault(mac.Replace(":", "")[..6]);
}
