using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Agents;

public sealed class AgentEnrollmentService(MonitoreoDbContext db, TenantContext tenant) : IAgentEnrollmentService
{
    public async Task<EnrollResult> EnrollAsync(EnrollRequest request, string? sourceIp, CancellationToken cancellationToken)
    {
        if (!AgentTokens.TryGetCompanyId(request.EnrollToken, out var companyId)
            || !MacAddress.TryNormalize(request.MacAddress, out var mac))
            return EnrollResult.Rejected;

        // La empresa sale del prefijo del token; si no corresponde al secreto, el hash no se encuentra en esa empresa.
        tenant.SetCompany(companyId);
        var tokenHash = AgentTokens.Hash(request.EnrollToken);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var token = await db.EnrollmentTokens.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
            if (token is null)
                return EnrollResult.Rejected;

            // Un solo UPDATE valida vencimiento y cupo y consume el uso: dos registros simultáneos no pueden pasar con un cupo.
            var claimed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE dbo.tokens_registro SET usos = usos + 1
                WHERE id = {token.Id} AND revocado IS NULL AND vence > SYSUTCDATETIME() AND (usos_max IS NULL OR usos < usos_max)
                """, cancellationToken);
            if (claimed == 0)
                return EnrollResult.Rejected;

            // El mismo equipo puede volver con otra tarjeta como principal (Wi-Fi en vez de cable):
            // se reconoce por la MAC principal o por cualquiera de las tarjetas que ya reportó.
            var machine = await db.Machines.Include(m => m.Agent)
                    .FirstOrDefaultAsync(m => m.MacAddress == mac, cancellationToken)
                ?? await db.Machines.Include(m => m.Agent)
                    .FirstOrDefaultAsync(m => db.NetworkInterfaces.Any(i => i.MachineId == m.Id && i.MacAddress == mac), cancellationToken);
            var isReinstall = machine is not null;

            if (machine is null)
            {
                machine = new Machine();
                db.Machines.Add(machine);
            }

            machine.MacAddress = mac;

            // El token de la sucursal es la autoridad: una reinstalación puede mover el equipo de sucursal.
            machine.BranchId = token.BranchId;
            machine.Name = request.Hostname.Trim();
            machine.Type = ParseType(request.MachineType);
            machine.IpAddress = request.IpAddress;
            machine.OperatingSystem = request.OperatingSystem;
            machine.Brand = request.Brand ?? machine.Brand;
            machine.Model = request.Model ?? machine.Model;
            machine.SerialNumber = request.SerialNumber ?? machine.SerialNumber;
            if (machine.Status == MachineStatus.Retired)
                machine.Status = MachineStatus.Disconnected;

            var (credential, credentialHash) = AgentTokens.Generate(companyId);
            var agent = machine.Agent;
            if (agent is null)
            {
                agent = new Agent();
                machine.Agent = agent;
            }

            // Reinstalar rota la credencial: la anterior deja de valer.
            agent.TokenHash = credentialHash;
            agent.Version = request.AgentVersion;
            agent.IsRevoked = false;
            // Con un token que exige aprobación, un equipo nuevo queda pendiente (W-19); uno ya aprobado lo sigue estando.
            agent.IsApproved = !token.RequiresApproval || (isReinstall && agent.Id != 0 && agent.IsApproved);

            await db.SaveChangesAsync(cancellationToken);

            // Instalación remota (decisión 77): el equipo que llega con el token de esa instalación la da por terminada.
            await db.RemoteInstalls.Where(r => r.EnrollmentTokenId == token.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, "registrado")
                    .SetProperty(r => r.MachineId, machine.Id)
                    .SetProperty(r => r.Detail, (string?)null)
                    .SetProperty(r => r.UpdatedAt, DateTime.UtcNow), cancellationToken);

            db.AuditLogs.Add(new AuditLog
            {
                Action = isReinstall ? "agente.reinstalacion" : "agente.registro",
                Entity = "agentes",
                EntityId = agent.Id.ToString(),
                Details = JsonSerializer.Serialize(new
                {
                    machineId = machine.Id,
                    branchId = token.BranchId,
                    hostname = machine.Name,
                    mac,
                    version = request.AgentVersion,
                }),
                SourceIp = sourceIp,
            });
            await db.SaveChangesAsync(cancellationToken);

            var publicKey = await db.CompanyKeys.AsNoTracking()
                .Where(k => k.IsActive)
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => k.PublicKey)
                .FirstOrDefaultAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new EnrollResult(true, machine.Id, agent.Id, credential, publicKey, agent.IsApproved);
        });
    }

    private static MachineType ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "laptop" => MachineType.Laptop,
        "servidor" => MachineType.Server,
        _ => MachineType.Desktop,
    };
}
