using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Escrituras del panel. La empresa la asigna el contexto (nunca el cuerpo de la petición) y toda referencia
/// (sucursal, departamento, responsable) se valida dentro de la empresa y del alcance de sucursales del usuario.
/// Cada cambio queda en auditoría con los valores de antes y después.
/// </summary>
public sealed class PanelCommands(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<bool> CanUseBranchAsync(int branchId, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        return (allowed is null || allowed.Contains(branchId)) && await db.Branches.AnyAsync(b => b.Id == branchId, ct);
    }

    private async Task<Machine?> FindMachineAsync(int id, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var machine = await db.Machines.FirstOrDefaultAsync(m => m.Id == id, ct);
        return machine is null || (allowed is not null && !allowed.Contains(machine.BranchId)) ? null : machine;
    }

    // ------------------------------------------------------------------ equipos

    public async Task<CommandResult> CreateMachineAsync(MachineInput input, CancellationToken ct)
    {
        var invalid = await ValidateMachineReferencesAsync(input, ct);
        if (invalid is not null)
            return invalid;

        var machine = new Machine();
        Apply(machine, input);
        // Un equipo cargado a mano no tiene agente: su estado no lo decide la presencia.
        machine.Status = MachineStatus.Healthy;
        db.Machines.Add(machine);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("maquina.crear", "maquinas", machine.Id.ToString(), null, Snapshot(machine), ct);
        return CommandResult.Ok(machine.Id);
    }

    public async Task<CommandResult> UpdateMachineAsync(int id, MachineInput input, CancellationToken ct)
    {
        var machine = await FindMachineAsync(id, ct);
        if (machine is null || machine.Status == MachineStatus.Retired)
            return CommandResult.NotFound();

        var invalid = await ValidateMachineReferencesAsync(input, ct);
        if (invalid is not null)
            return invalid;

        var before = Snapshot(machine);
        Apply(machine, input);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("maquina.editar", "maquinas", id.ToString(), before, Snapshot(machine), ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Dar de baja: el equipo no se borra (conserva su historial) y su agente queda revocado.</summary>
    public async Task<CommandResult> RetireMachineAsync(int id, CancellationToken ct)
    {
        var machine = await FindMachineAsync(id, ct);
        if (machine is null || machine.Status == MachineStatus.Retired)
            return CommandResult.NotFound();

        var before = Snapshot(machine);
        machine.Status = MachineStatus.Retired;
        await db.Agents.Where(a => a.MachineId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRevoked, true), ct);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("maquina.baja", "maquinas", id.ToString(), before, Snapshot(machine), ct);
        return CommandResult.Ok(id);
    }

    private async Task<CommandResult?> ValidateMachineReferencesAsync(MachineInput input, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(input.SucursalId, ct))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        if (input.DepartamentoId is int departmentId && !await db.Departments.AnyAsync(d => d.Id == departmentId, ct))
            return CommandResult.Invalid("El departamento no existe.");
        return null;
    }

    private static void Apply(Machine machine, MachineInput input)
    {
        machine.Name = input.Nombre.Trim();
        machine.Type = input.Tipo switch
        {
            "laptop" => MachineType.Laptop,
            "servidor" => MachineType.Server,
            "impresora" => MachineType.Printer,
            "otro" => MachineType.Other,
            _ => MachineType.Desktop,
        };
        machine.BranchId = input.SucursalId;
        machine.DepartmentId = input.DepartamentoId;
        machine.Brand = Clean(input.Marca);
        machine.Model = Clean(input.Modelo);
        machine.SerialNumber = Clean(input.Serie);
        machine.OperatingSystem = Clean(input.SistemaOperativo);
        machine.IpAddress = Clean(input.Ip);
        machine.MacAddress = MacAddress.TryNormalize(input.Mac, out var mac) ? mac : null;
        machine.AssignedTo = Clean(input.Responsable);
        machine.PurchaseDate = input.FechaCompra;
        machine.WarrantyUntil = input.GarantiaHasta;
    }

    private static object Snapshot(Machine m) => new
    {
        nombre = m.Name,
        tipo = PanelQueries.Text(m.Type),
        estado = PanelQueries.Text(m.Status),
        sucursalId = m.BranchId,
        departamentoId = m.DepartmentId,
        marca = m.Brand,
        modelo = m.Model,
        serie = m.SerialNumber,
        so = m.OperatingSystem,
        ip = m.IpAddress,
        mac = m.MacAddress,
        responsable = m.AssignedTo,
        fechaCompra = m.PurchaseDate,
        garantiaHasta = m.WarrantyUntil,
    };

    // ------------------------------------------------------------------ sucursales

    public async Task<CommandResult> CreateBranchAsync(BranchInput input, CancellationToken ct)
    {
        var name = input.Nombre.Trim();
        if (await db.Branches.AnyAsync(b => b.Name == name, ct))
            return CommandResult.Conflict($"Ya existe una sucursal llamada \"{name}\".");
        if (!await IsCompanyUserAsync(input.ResponsableId, ct))
            return CommandResult.Invalid("El responsable no pertenece a la empresa.");

        var branch = new Branch();
        Apply(branch, input);
        db.Branches.Add(branch);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("sucursal.crear", "sucursales", branch.Id.ToString(), null, Snapshot(branch), ct);
        return CommandResult.Ok(branch.Id);
    }

    public async Task<CommandResult> UpdateBranchAsync(int id, BranchInput input, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (branch is null || (allowed is not null && !allowed.Contains(id)))
            return CommandResult.NotFound();

        var name = input.Nombre.Trim();
        if (await db.Branches.AnyAsync(b => b.Name == name && b.Id != id, ct))
            return CommandResult.Conflict($"Ya existe una sucursal llamada \"{name}\".");
        if (!await IsCompanyUserAsync(input.ResponsableId, ct))
            return CommandResult.Invalid("El responsable no pertenece a la empresa.");

        var before = Snapshot(branch);
        Apply(branch, input);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("sucursal.editar", "sucursales", id.ToString(), before, Snapshot(branch), ct);
        return CommandResult.Ok(id);
    }

    /// <summary>usuario_empresa no tiene filtro por empresa: se compara con la empresa de la sesión.</summary>
    private async Task<bool> IsCompanyUserAsync(int? userId, CancellationToken ct) =>
        userId is not int id || await db.UserCompanies.AnyAsync(u => u.UserId == id && u.CompanyId == user.CompanyId && u.IsActive, ct);

    private static void Apply(Branch branch, BranchInput input)
    {
        branch.Name = input.Nombre.Trim();
        branch.Province = Clean(input.Provincia);
        branch.Address = Clean(input.Direccion);
        branch.ManagerId = input.ResponsableId;
        branch.IsActive = input.Activa;
    }

    private static object Snapshot(Branch b) => new
    {
        nombre = b.Name,
        provincia = b.Province,
        direccion = b.Address,
        responsableId = b.ManagerId,
        activa = b.IsActive,
    };

    // ------------------------------------------------------------------ instaladores y aprobación

    public async Task<(CommandResult Result, CreatedEnrollmentToken? Token)> CreateEnrollmentTokenAsync(EnrollmentTokenInput input, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(input.SucursalId, ct))
            return (CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella."), null);

        var (token, hash) = AgentTokens.Generate(user.CompanyId);
        var entity = new EnrollmentToken
        {
            BranchId = input.SucursalId,
            Name = Clean(input.Nombre),
            TokenHash = hash,
            ExpiresAt = Now.AddDays(input.DiasVigencia),
            MaxUses = input.UsosMax,
            RequiresApproval = input.RequiereAprobacion,
            CreatedById = user.UserId,
        };
        db.EnrollmentTokens.Add(entity);
        await db.SaveChangesAsync(ct);

        // El token en claro no se guarda ni se audita: solo se muestra una vez al crearlo.
        await audit.RecordAsync("instalador.crear", "tokens_registro", entity.Id.ToString(), null, new
        {
            sucursalId = entity.BranchId,
            nombre = entity.Name,
            vence = entity.ExpiresAt,
            usosMax = entity.MaxUses,
            requiereAprobacion = entity.RequiresApproval,
        }, ct);

        return (CommandResult.Ok(entity.Id), new CreatedEnrollmentToken(entity.Id, token, entity.ExpiresAt));
    }

    public async Task<CommandResult> RevokeEnrollmentTokenAsync(int id, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (token is null || (allowed is not null && !allowed.Contains(token.BranchId)))
            return CommandResult.NotFound();
        if (token.RevokedAt is not null)
            return CommandResult.Ok(id);

        token.RevokedAt = Now;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("instalador.revocar", "tokens_registro", id.ToString(), null, null, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>
    /// Borra un token de la lista. Los equipos que ya registró no se ven afectados: tienen su propia credencial.
    /// La auditoría conserva que existió.
    /// </summary>
    public async Task<CommandResult> DeleteEnrollmentTokenAsync(int id, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (token is null || (allowed is not null && !allowed.Contains(token.BranchId)))
            return CommandResult.NotFound();

        var before = new { sucursalId = token.BranchId, nombre = token.Name, usos = token.Uses, vence = token.ExpiresAt };
        db.EnrollmentTokens.Remove(token);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("instalador.eliminar", "tokens_registro", id.ToString(), before, null, ct);
        return CommandResult.Ok(id);
    }

    public async Task<IReadOnlyList<EnrollmentTokenItem>> EnrollmentTokensAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var query = db.EnrollmentTokens.AsNoTracking();
        if (allowed is not null)
            query = query.Where(t => allowed.Contains(t.BranchId));

        var now = Now;
        var rows = await query.OrderByDescending(t => t.CreatedAt).Take(200)
            .Select(t => new
            {
                t,
                Branch = db.Branches.Where(b => b.Id == t.BranchId).Select(b => b.Name).FirstOrDefault(),
                CreatedBy = db.Users.Where(u => u.Id == t.CreatedById).Select(u => u.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new EnrollmentTokenItem(
            r.t.Id, r.t.Name, r.t.BranchId, r.Branch ?? "—", r.t.ExpiresAt, r.t.MaxUses, r.t.Uses, r.t.RequiresApproval,
            r.t.RevokedAt, r.t.CreatedAt, r.CreatedBy,
            r.t.RevokedAt is not null ? "revocado"
                : r.t.ExpiresAt <= now ? "vencido"
                : r.t.MaxUses is int max && r.t.Uses >= max ? "agotado"
                : "activo")).ToList();
    }

    public async Task<IReadOnlyList<PendingAgentItem>> PendingAgentsAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var query = db.Machines.AsNoTracking().Where(m => m.Agent != null && !m.Agent.IsApproved && !m.Agent.IsRevoked
            && m.Status != MachineStatus.Retired);
        if (allowed is not null)
            query = query.Where(m => allowed.Contains(m.BranchId));

        return await query.OrderBy(m => m.Agent!.CreatedAt)
            .Select(m => new PendingAgentItem(m.Agent!.Id, m.Id, m.Name, m.IpAddress, m.MacAddress, m.OperatingSystem,
                m.Branch!.Name, m.Agent.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Aprobar deja conectar al agente; rechazar lo revoca y da de baja el equipo.</summary>
    public async Task<CommandResult> DecidePendingAgentAsync(int agentId, bool approve, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId && !a.IsApproved && !a.IsRevoked, ct);
        var machine = agent is null ? null : await db.Machines.FirstOrDefaultAsync(m => m.Id == agent.MachineId, ct);
        if (agent is null || machine is null || (allowed is not null && !allowed.Contains(machine.BranchId)))
            return CommandResult.NotFound();

        if (approve)
        {
            agent.IsApproved = true;
        }
        else
        {
            agent.IsRevoked = true;
            machine.Status = MachineStatus.Retired;
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(approve ? "agente.aprobado" : "agente.rechazado", "maquinas", machine.Id.ToString(), null,
            new { agenteId = agentId, nombre = machine.Name, mac = machine.MacAddress }, ct);
        return CommandResult.Ok(machine.Id);
    }

    // ------------------------------------------------------------------ catálogos para formularios

    public async Task<IReadOnlyList<object>> DepartmentsAsync(CancellationToken ct) =>
        await db.Departments.AsNoTracking().OrderBy(d => d.Name).Select(d => (object)new { id = d.Id, nombre = d.Name }).ToListAsync(ct);

    /// <summary>Usuarios activos de la empresa (para elegir el responsable de una sucursal).</summary>
    public async Task<IReadOnlyList<object>> CompanyUsersAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        return await db.UserCompanies.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.IsActive)
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.Name })
            .OrderBy(u => u.Name)
            .Select(u => (object)new { id = u.Id, nombre = u.Name })
            .ToListAsync(ct);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
