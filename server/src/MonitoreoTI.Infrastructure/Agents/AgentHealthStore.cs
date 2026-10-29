using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Application.Agents.TelemetryRules;

namespace MonitoreoTI.Infrastructure.Agents;

/// <summary>
/// Guarda los datos de salud que manda el agente (Fase 3). Las listas completas (volúmenes, servicios,
/// antivirus) reemplazan a las anteriores; los eventos y las fotos de procesos se agregan.
/// </summary>
public sealed class AgentHealthStore(IServiceScopeFactory scopes, TimeProvider clock) : IAgentHealthStore
{
    /// <summary>Fotos de procesos que se conservan por equipo: un día.</summary>
    private static readonly TimeSpan ProcessHistory = TimeSpan.FromDays(1);

    public const int MaxVolumes = 26;
    public const int MaxProcesses = 50;
    public const int MaxServices = 1000;
    public const int MaxEvents = 200;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task SaveVolumesAsync(AgentIdentity identity, VolumenesData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            var reported = (data.Volumenes ?? [])
                .Where(v => Trim(v.Unidad, 10) is { } drive && System.Text.RegularExpressions.Regex.IsMatch(drive, "^[A-Za-z]:$")
                    && v.TotalGb > 0 && v.LibreGb >= 0 && v.LibreGb <= v.TotalGb)
                .Take(MaxVolumes)
                .GroupBy(v => v.Unidad!.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            var existing = await db.Volumes.Where(v => v.MachineId == identity.MachineId).ToListAsync(cancellationToken);
            db.Volumes.RemoveRange(existing.Where(v => !reported.ContainsKey(v.Drive)));

            foreach (var (drive, item) in reported)
            {
                var row = existing.FirstOrDefault(v => v.Drive == drive);
                if (row is null)
                {
                    row = new Volume { MachineId = identity.MachineId, Drive = drive };
                    db.Volumes.Add(row);
                }
                row.Label = Trim(item.Etiqueta, 100);
                row.TotalGb = Math.Round(Math.Min(item.TotalGb, 999_999_999m), 1);
                row.FreeGb = Math.Round(Math.Min(item.LibreGb, row.TotalGb), 1);
                row.UpdatedAt = Now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    public Task SaveProcessesAsync(AgentIdentity identity, ProcesosData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            var now = Now;
            int? total = data.Total is >= 0 and < 100_000 ? data.Total : null;
            await db.Machines.Where(m => m.Id == identity.MachineId)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProcessCount, total), cancellationToken);

            db.ProcessSnapshots.AddRange((data.Procesos ?? []).Take(MaxProcesses)
                .Where(p => Trim(p.Nombre, 260) is not null)
                .Select(p => new ProcessSnapshot
                {
                    MachineId = identity.MachineId,
                    Timestamp = now,
                    Name = Trim(p.Nombre, 260)!,
                    Pid = p.Pid > 0 ? p.Pid : null,
                    Cpu = Percent(p.Cpu),
                    RamMb = p.RamMb is >= 0 and < 10_000_000 ? Math.Round(p.RamMb, 2) : null,
                }));
            await db.SaveChangesAsync(cancellationToken);

            var cutoff = now - ProcessHistory;
            await db.ProcessSnapshots.Where(p => p.MachineId == identity.MachineId && p.Timestamp < cutoff)
                .ExecuteDeleteAsync(cancellationToken);
        }, cancellationToken);

    public Task SaveServicesAsync(AgentIdentity identity, ServiciosData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            var reported = (data.Servicios ?? [])
                .Where(s => Trim(s.Nombre, 256) is not null)
                .Take(MaxServices)
                .GroupBy(s => s.Nombre!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var existing = await db.Services.Where(s => s.MachineId == identity.MachineId).ToListAsync(cancellationToken);
            db.Services.RemoveRange(existing.Where(s => !reported.ContainsKey(s.Name)));

            foreach (var (name, item) in reported)
            {
                var state = item.Estado is "en_ejecucion" or "detenido" ? item.Estado : "otro";
                var start = item.Inicio is "automatico" or "automatico_retrasado" or "manual" or "deshabilitado" ? item.Inicio : "automatico";
                var row = existing.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    // Un servicio que ya llega detenido no es una caída: muchos automáticos se detienen solos
                    // (actualizadores, servicios por demanda). Solo cuenta el que se detiene después de estar en ejecución.
                    row = new WindowsService { MachineId = identity.MachineId, Name = Trim(name, 256)! };
                    db.Services.Add(row);
                }
                else if (row.State == "en_ejecucion" && state == "detenido" && start.StartsWith("automatico", StringComparison.Ordinal))
                {
                    row.StoppedSince = Now;
                }

                if (state == "en_ejecucion")
                    row.StoppedSince = null;

                row.DisplayName = Trim(item.NombreVisible, 256);
                row.State = state;
                row.StartMode = start;
                row.UpdatedAt = Now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    public Task SaveEventsAsync(AgentIdentity identity, EventosData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            var now = Now;
            var events = (data.Eventos ?? []).Take(MaxEvents)
                .Select(e => new ErrorEvent
                {
                    MachineId = identity.MachineId,
                    // La hora real del evento (puede ser de antes de que el agente se conectara), dentro de lo creíble.
                    Timestamp = e.Fecha is { } date ? SampleTime(date, now) : now,
                    Source = Trim(e.Origen, 200),
                    Code = e.Codigo,
                    Level = e.Nivel == "critico" ? EventLevel.Critical : EventLevel.Error,
                    Message = Trim(e.Mensaje, 4000),
                    SimpleMessage = EventCatalog.Explain(e.Origen, e.Codigo)?.Title,
                })
                .ToList();
            if (events.Count == 0)
                return;

            // Un reenvío (ack perdido) no duplica: se descartan los que ya existen con la misma hora, origen y código.
            var from = events.Min(e => e.Timestamp);
            var to = events.Max(e => e.Timestamp);
            var existing = await db.ErrorEvents.AsNoTracking()
                .Where(e => e.MachineId == identity.MachineId && e.Timestamp >= from && e.Timestamp <= to)
                .Select(e => new { e.Timestamp, e.Source, e.Code })
                .ToListAsync(cancellationToken);
            var seen = existing.Select(e => (e.Timestamp, e.Source, e.Code)).ToHashSet();

            db.ErrorEvents.AddRange(events.Where(e => seen.Add((e.Timestamp, e.Source, e.Code))));
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    public Task SaveDiskHealthAsync(AgentIdentity identity, SaludDiscosData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            var disks = await db.Components.Where(c => c.MachineId == identity.MachineId && c.Type == "disco").ToListAsync(cancellationToken);

            foreach (var item in (data.Discos ?? []).Take(16))
            {
                var model = Trim(item.Modelo, 200);
                if (model is null)
                    continue;

                // Se asocia con el disco del inventario de hardware por su modelo; si aún no llegó el inventario, se crea.
                var component = disks.FirstOrDefault(c => c.Description is not null
                    && (c.Description.Contains(model, StringComparison.OrdinalIgnoreCase) || model.Contains(c.Description, StringComparison.OrdinalIgnoreCase)));
                if (component is null)
                {
                    component = new Component
                    {
                        MachineId = identity.MachineId,
                        Type = "disco",
                        Description = model,
                        Capacity = item.TamanoGb is > 0 ? $"{item.TamanoGb} GB" : null,
                    };
                    db.Components.Add(component);
                    disks.Add(component);
                }

                component.HealthStatus = item.Salud switch
                {
                    "sano" => HealthStatus.Healthy,
                    "advertencia" => HealthStatus.Warning,
                    "critico" => HealthStatus.Critical,
                    _ => HealthStatus.Unknown,
                };
                component.Detail = Trim(DiskDetail(item), 200);
                component.UpdatedAt = Now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    /// <summary>"148 sectores dañados", "Desgaste 12 % · 41 °C"… lo más importante primero.</summary>
    public static string? DiskDetail(DiscoSaludData disk)
    {
        var parts = new List<string>();
        if (disk.SectoresReasignados is > 0)
            parts.Add($"{disk.SectoresReasignados} sectores dañados");
        if (disk.SectoresPendientes is > 0)
            parts.Add($"{disk.SectoresPendientes} sectores pendientes");
        if (disk.ErroresLectura is > 0)
            parts.Add($"{disk.ErroresLectura} errores de lectura");
        if (disk.Desgaste is > 0)
            parts.Add($"Desgaste {disk.Desgaste:0} %");
        if (disk.Temperatura is > 0 and < 150)
            parts.Add($"{disk.Temperatura:0} °C");
        if (parts.Count == 0 && disk.Salud == "sano")
            parts.Add("Sin errores");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    public Task SaveSecurityAsync(AgentIdentity identity, SeguridadData data, CancellationToken cancellationToken) =>
        InCompanyAsync(identity, async db =>
        {
            // Antivirus y firewall se guardan como componentes: aparecen en "Salud de componentes".
            await db.Components.Where(c => c.MachineId == identity.MachineId && (c.Type == "antivirus" || c.Type == "firewall"))
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var av in (data.Antivirus ?? []).Take(10).Where(a => Trim(a.Nombre, 200) is not null))
            {
                db.Components.Add(new Component
                {
                    MachineId = identity.MachineId,
                    Type = "antivirus",
                    Description = Trim(av.Nombre, 200),
                    HealthStatus = !av.Activo ? HealthStatus.Critical : !av.Actualizado ? HealthStatus.Warning : HealthStatus.Healthy,
                    Detail = !av.Activo ? "Desactivado" : !av.Actualizado ? "Firmas desactualizadas" : "Activo y al día",
                    UpdatedAt = Now,
                });
            }

            if (data.FirewallActivo is bool firewall)
            {
                db.Components.Add(new Component
                {
                    MachineId = identity.MachineId,
                    Type = "firewall",
                    Description = "Firewall de Windows",
                    HealthStatus = firewall ? HealthStatus.Healthy : HealthStatus.Warning,
                    Detail = firewall ? "Activo" : "Desactivado",
                    UpdatedAt = Now,
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);

    private async Task InCompanyAsync(AgentIdentity identity, Func<MonitoreoDbContext, Task> work, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(identity.CompanyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await work(db);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
