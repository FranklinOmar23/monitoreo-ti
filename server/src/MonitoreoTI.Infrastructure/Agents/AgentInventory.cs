using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Network;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Application.Agents.TelemetryRules;

namespace MonitoreoTI.Infrastructure.Agents;

/// <summary>
/// Guarda los inventarios del agente. Cada inventario es la lista completa y reemplaza al anterior,
/// dentro de una transacción: nunca queda un inventario a medias. La máquina y la empresa salen de la
/// identidad autenticada, jamás del mensaje.
/// </summary>
public sealed class AgentInventory(IServiceScopeFactory scopes, OuiDirectory oui) : IAgentInventory
{
    /// <summary>Tipos de componente que manda el inventario de hardware (el resto llega por otros mensajes).</summary>
    private static readonly string[] HardwareComponentTypes = ["cpu", "ram", "disco", "gpu", "placa_base", "bios", "bateria", "ventilador"];

    public async Task SaveHardwareAsync(AgentIdentity identity, InventarioHardwareData data, CancellationToken cancellationToken)
    {
        var components = (data.Componentes ?? [])
            .Take(MaxComponents)
            .Select(c => new
            {
                Type = Trim(c.Tipo, 30),
                Description = Trim(c.Descripcion, 200),
                Capacity = Trim(c.Capacidad, 50),
                Health = ParseHealth(c.EstadoSalud),
            })
            .Where(c => c.Type is not null)
            .ToList();

        await InTransactionAsync(identity, async db =>
        {
            var machine = await db.Machines.FirstAsync(m => m.Id == identity.MachineId, cancellationToken);
            machine.Brand = Trim(data.Fabricante, 100) ?? machine.Brand;
            machine.Model = Trim(data.Modelo, 100) ?? machine.Model;
            machine.SerialNumber = Trim(data.Serie, 100) ?? machine.SerialNumber;
            machine.OperatingSystem = Trim(data.SistemaOperativo, 150) ?? machine.OperatingSystem;
            machine.Type = data.Tipo?.Trim().ToLowerInvariant() switch
            {
                "laptop" => MachineType.Laptop,
                "servidor" => MachineType.Server,
                "computadora" => MachineType.Desktop,
                _ => machine.Type,
            };

            if (data.RamTotalMb is > 0 and < 100_000_000)
                machine.RamTotalMb = data.RamTotalMb;

            // Solo se reemplazan los componentes que describe el inventario de hardware. El antivirus, el firewall
            // y la salud SMART de los discos llegan en otros mensajes y se conservan.
            var existing = await db.Components
                .Where(c => c.MachineId == identity.MachineId && HardwareComponentTypes.Contains(c.Type))
                .ToListAsync(cancellationToken);
            var previousDisks = existing.Where(c => c.Type == "disco" && c.Description != null)
                .GroupBy(c => c.Description!).ToDictionary(g => g.Key, g => g.First());
            db.Components.RemoveRange(existing);

            db.Components.AddRange(components.Select(c =>
            {
                var health = c.Health;
                string? detail = null;
                if (c.Type == "disco" && c.Description is not null && previousDisks.TryGetValue(c.Description, out var previous))
                {
                    health = previous.HealthStatus;
                    detail = previous.Detail;
                }
                return new Component
                {
                    MachineId = identity.MachineId,
                    Type = c.Type!,
                    Description = c.Description,
                    Capacity = c.Capacity,
                    HealthStatus = health,
                    Detail = detail,
                };
            }));
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task SaveSoftwareAsync(AgentIdentity identity, InventarioSoftwareData data, CancellationToken cancellationToken)
    {
        var programs = (data.Programas ?? [])
            .Take(MaxPrograms)
            .Select(p => new InstalledSoftware
            {
                MachineId = identity.MachineId,
                Name = Trim(p.Nombre, 250)!,
                Version = Trim(p.Version, 100),
                Publisher = Trim(p.Editor, 200),
                InstalledOn = TryParseDate(p.FechaInstalacion, out var date) ? date : null,
            })
            .Where(p => p.Name is not null)
            .ToList();

        await InTransactionAsync(identity, async db =>
        {
            await db.InstalledSoftware.Where(s => s.MachineId == identity.MachineId).ExecuteDeleteAsync(cancellationToken);
            db.InstalledSoftware.AddRange(programs);
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task SaveInterfacesAsync(AgentIdentity identity, InterfacesRedData data, CancellationToken cancellationToken)
    {
        var reported = new Dictionary<string, InterfazRedData>();
        foreach (var item in (data.Interfaces ?? []).Take(MaxInterfaces))
        {
            if (MacAddress.TryNormalize(item.Mac, out var mac) && mac != "00:00:00:00:00:00" && !string.IsNullOrWhiteSpace(item.Nombre))
                reported[mac] = item;
        }

        await InTransactionAsync(identity, async db =>
        {
            var macs = reported.Keys.ToList();

            // La MAC es única por empresa: si ya figura en otro equipo (tarjeta cambiada de PC, clon de VM), pasa a este.
            var existing = await db.NetworkInterfaces
                .Where(i => i.MachineId == identity.MachineId || macs.Contains(i.MacAddress))
                .ToListAsync(cancellationToken);

            foreach (var stale in existing.Where(i => i.MachineId == identity.MachineId && !reported.ContainsKey(i.MacAddress)))
                db.NetworkInterfaces.Remove(stale);

            foreach (var (mac, item) in reported)
            {
                var row = existing.FirstOrDefault(i => i.MacAddress == mac);
                if (row is null)
                {
                    row = new NetworkInterface { MacAddress = mac };
                    db.NetworkInterfaces.Add(row);
                }

                row.MachineId = identity.MachineId;
                row.Name = Trim(item.Nombre, 150)!;
                row.IpAddress = Trim(item.Ip, 45);
                row.Type = ParseInterfaceType(item.Tipo);
                row.SpeedMbps = item.VelocidadMbps is > 0 ? item.VelocidadMbps : null;
                row.UpdatedAt = DateTime.UtcNow;
            }

            // La IP de la ficha del equipo sigue a su tarjeta principal (el DHCP la cambia).
            var machine = await db.Machines.FirstAsync(m => m.Id == identity.MachineId, cancellationToken);
            if (machine.MacAddress is not null && reported.TryGetValue(machine.MacAddress, out var primary) && Trim(primary.Ip, 45) is { } ip)
                machine.IpAddress = ip;

            // Historial de direcciones IP (decisión 72): este equipo tiene estas IP ahora.
            var seen = reported
                .Select(r => IpRules.TryParse(r.Value.Ip, out var address) ? new IpInventory.Observation(address, r.Key, false, identity.MachineId, FromAgent: true) : null)
                .OfType<IpInventory.Observation>()
                .ToList();
            await IpInventory.RecordAsync(db, oui, seen, null, DateTime.UtcNow, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private async Task InTransactionAsync(AgentIdentity identity, Func<MonitoreoDbContext, Task> work, CancellationToken cancellationToken)
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

    private static HealthStatus ParseHealth(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sano" => HealthStatus.Healthy,
        "advertencia" => HealthStatus.Warning,
        "critico" => HealthStatus.Critical,
        _ => HealthStatus.Unknown,
    };

    private static NetworkInterfaceType ParseInterfaceType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cableada" => NetworkInterfaceType.Wired,
        "wifi" => NetworkInterfaceType.Wifi,
        "virtual" => NetworkInterfaceType.Virtual,
        _ => NetworkInterfaceType.Other,
    };
}
