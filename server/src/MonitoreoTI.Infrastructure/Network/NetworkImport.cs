using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Importación masiva desde CSV (W-68, sección 11.2): dispositivos, rosetas o cables. Cada fila se traduce de
/// nombres a ids (sucursal, modelo, gabinete, puerto, roseta) y pasa por el mismo alta que el panel, con sus
/// validaciones y su auditoría. Todo corre en una transacción: la vista previa siempre se deshace, y la importación
/// solo se confirma si ninguna fila tiene errores (así las filas se validan también entre sí: dos equipos en la
/// misma U, dos rosetas con el mismo código).
/// </summary>
public sealed class NetworkImport(
    MonitoreoDbContext db,
    NetworkQueries queries,
    NetworkCommands commands,
    IAuditTrail audit,
    IValidator<DeviceInput> deviceValidator,
    IValidator<OutletInput> outletValidator,
    IValidator<CableInput> cableValidator)
{
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public async Task<ImportResult> RunAsync(ImportInput input, bool apply, CancellationToken ct)
    {
        var table = CsvTable.Parse(input.Contenido);
        var fileErrors = new List<string>();
        var missing = ImportColumns.Required(input.Tipo).Where(c => !table.Columns.Contains(c)).ToList();
        if (missing.Count > 0)
            fileErrors.Add($"Faltan columnas: {string.Join(", ", missing)}.");
        if (table.Rows.Count == 0)
            fileErrors.Add("El archivo no tiene filas.");
        if (table.Rows.Count > ImportColumns.MaxRows)
            fileErrors.Add($"El archivo tiene {table.Rows.Count} filas; el máximo es {ImportColumns.MaxRows}.");
        if (fileErrors.Count > 0)
            return new ImportResult(input.Tipo, table.Rows.Count, 0, false, fileErrors, []);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var lookups = await Lookups.LoadAsync(db, queries, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var rows = new List<ImportRow>();
            foreach (var row in table.Rows)
            {
                var (summary, errors) = input.Tipo switch
                {
                    "dispositivos" => await DeviceRowAsync(row, lookups, ct),
                    "rosetas" => await OutletRowAsync(row, lookups, ct),
                    _ => await CableRowAsync(row, lookups, ct),
                };
                if (errors.Count > 0)
                    db.ChangeTracker.Clear();
                rows.Add(new ImportRow(row.Line, summary, errors));
            }

            var valid = rows.Count(r => r.Errors.Count == 0);
            var applied = apply && valid == rows.Count;
            if (applied)
            {
                var (action, entity) = input.Tipo switch
                {
                    "dispositivos" => ("dispositivo.importar", "dispositivos_red"),
                    "rosetas" => ("roseta.importar", "puntos_red"),
                    _ => ("cable.importar", "cableado"),
                };
                await audit.RecordAsync(action, entity, null, null, new { filas = rows.Count }, ct);
                await transaction.CommitAsync(ct);
            }
            else
                await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return new ImportResult(input.Tipo, rows.Count, valid, applied, [], rows);
        });
    }

    // ------------------------------------------------------------------ filas

    private async Task<(string, List<string>)> DeviceRowAsync(CsvRow row, Lookups l, CancellationToken ct)
    {
        var errors = new List<string>();
        var name = row.Get("nombre") ?? "";
        var brand = row.Get("marca");
        var modelName = row.Get("modelo");
        var rackName = row.Get("gabinete");
        var position = row.Get("posicion_u");
        var summary = $"{name} · {brand} {modelName}" + (rackName is null ? "" : $" · {rackName} U{position}");

        var branch = l.Branch(row.Get("sucursal"), errors);
        // El modelo propio de la empresa gana sobre uno del catálogo base con el mismo nombre.
        var model = l.Models.Where(m => Same(m.Brand, brand) && Same(m.ModelName, modelName)).OrderBy(m => m.CompanyId is null).FirstOrDefault();
        if (model is null && brand is not null && modelName is not null)
            errors.Add($"El modelo {brand} {modelName} no está en el catálogo.");

        int? rackId = null;
        if (rackName is not null && branch is not null)
        {
            var room = row.Get("sala");
            var racks = l.Racks.Where(r => r.BranchId == branch.Id && Same(r.Name, rackName) && (room is null || Same(r.Room, room))).ToList();
            if (racks.Count == 0)
                errors.Add($"No hay un gabinete {rackName}{(room is null ? "" : $" en la sala {room}")} en {branch.Name}.");
            else if (racks.Count > 1)
                errors.Add($"Hay varios gabinetes {rackName} en {branch.Name}: indique la sala.");
            else
                rackId = racks[0].Id;
        }

        int? positionU = null;
        if (position is not null)
        {
            if (int.TryParse(position, out var u))
                positionU = u;
            else
                errors.Add("La posición U debe ser un número.");
        }

        if (errors.Count > 0 || branch is null || model is null)
            return (summary, errors);
        var input = new DeviceInput(name, model.Id, branch.Id, rackId, positionU, row.Get("ip_gestion"), row.Get("firmware"), null, null, null);
        return (summary, await ExecuteAsync(input, deviceValidator, () => commands.CreateDeviceAsync(input, ct), ct));
    }

    private async Task<(string, List<string>)> OutletRowAsync(CsvRow row, Lookups l, CancellationToken ct)
    {
        var errors = new List<string>();
        var code = row.Get("codigo") ?? "";
        var location = row.Get("ubicacion");
        var summary = location is null ? code : $"{code} · {location}";
        var branch = l.Branch(row.Get("sucursal"), errors);
        if (branch is null)
            return (summary, errors);
        var input = new OutletInput(code, location, branch.Id);
        return (summary, await ExecuteAsync(input, outletValidator, () => commands.SaveOutletAsync(null, input, ct), ct));
    }

    private async Task<(string, List<string>)> CableRowAsync(CsvRow row, Lookups l, CancellationToken ct)
    {
        var errors = new List<string>();
        var deviceA = row.Get("dispositivo_a");
        var portA = row.Get("puerto_a");
        var deviceB = row.Get("dispositivo_b");
        var portB = row.Get("puerto_b");
        var outletCode = row.Get("roseta");
        var summary = $"{deviceA} {portA} → " + (outletCode is not null ? $"Roseta {outletCode}" : $"{deviceB} {portB}");

        var a = l.Port(deviceA, portA, errors);
        Lookups.PortRow? b = null;
        if (deviceB is not null || portB is not null)
            b = l.Port(deviceB, portB, errors);
        int? outletId = null;
        if (outletCode is not null && a is not null)
        {
            var outlet = l.Outlets.FirstOrDefault(o => o.BranchId == a.BranchId && Same(o.Code, outletCode));
            if (outlet is null)
                errors.Add($"No hay una roseta {outletCode} en la sucursal de {deviceA}.");
            else
                outletId = outlet.Id;
        }
        if (errors.Count > 0 || a is null)
            return (summary, errors);

        var input = new CableInput(a.Id, b?.Id, outletId, row.Get("tipo_cable")?.ToLowerInvariant(), row.Get("color"), row.Get("etiqueta"), row.Get("notas"));
        return (summary, await ExecuteAsync(input, cableValidator, () => commands.CreateCableAsync(input, ct), ct));
    }

    private static async Task<List<string>> ExecuteAsync<T>(T input, IValidator<T> validator, Func<Task<CommandResult>> run, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        if (!validation.IsValid)
            return validation.Errors.Select(e => e.ErrorMessage).Distinct().ToList();
        var result = await run();
        return result.Succeeded ? [] : [result.Message ?? "No encontrado."];
    }

    // ------------------------------------------------------------------ búsqueda por nombre

    /// <summary>Lo que el archivo nombra, cargado una vez y limitado al alcance del usuario.</summary>
    private sealed class Lookups
    {
        public sealed record BranchRow(int Id, string Name);
        public sealed record ModelRow(int Id, int? CompanyId, string Brand, string ModelName);
        public sealed record RackRow(int Id, string Name, string Room, int BranchId);
        public sealed record DeviceRow(int Id, string Name, int BranchId);
        public sealed record PortRow(int Id, int DeviceId, string? Name, int Number, int BranchId);
        public sealed record OutletRow(int Id, string Code, int BranchId);

        public required List<BranchRow> Branches { get; init; }
        public required List<ModelRow> Models { get; init; }
        public required List<RackRow> Racks { get; init; }
        public required List<DeviceRow> Devices { get; init; }
        public required List<PortRow> Ports { get; init; }
        public required List<OutletRow> Outlets { get; init; }

        public static async Task<Lookups> LoadAsync(MonitoreoDbContext db, NetworkQueries queries, CancellationToken ct)
        {
            var allowed = await queries.AllowedBranchesAsync(ct);
            var devices = await (await queries.VisibleDevicesAsync(ct)).AsNoTracking()
                .Select(d => new DeviceRow(d.Id, d.Name, d.BranchId)).ToListAsync(ct);
            var deviceIds = devices.Select(d => d.Id).ToList();
            var ports = await db.Ports.AsNoTracking().Where(p => deviceIds.Contains(p.DeviceId))
                .Select(p => new { p.Id, p.DeviceId, p.Name, p.Number }).ToListAsync(ct);
            var branchOf = devices.ToDictionary(d => d.Id, d => d.BranchId);

            return new Lookups
            {
                Branches = await db.Branches.AsNoTracking().Where(b => allowed == null || allowed.Contains(b.Id))
                    .Select(b => new BranchRow(b.Id, b.Name)).ToListAsync(ct),
                Models = await db.DeviceModels.AsNoTracking().Select(m => new ModelRow(m.Id, m.CompanyId, m.Brand, m.ModelName)).ToListAsync(ct),
                Racks = await (await queries.VisibleRacksAsync(ct)).AsNoTracking()
                    .Select(r => new RackRow(r.Id, r.Name, r.Room!.Name, r.Room.BranchId)).ToListAsync(ct),
                Devices = devices,
                Ports = ports.Select(p => new PortRow(p.Id, p.DeviceId, p.Name, p.Number, branchOf[p.DeviceId])).ToList(),
                Outlets = await (await queries.VisibleOutletsAsync(ct)).AsNoTracking()
                    .Select(o => new OutletRow(o.Id, o.Code, o.BranchId)).ToListAsync(ct),
            };
        }

        public BranchRow? Branch(string? name, List<string> errors)
        {
            if (name is null)
                return null;
            var branch = Branches.FirstOrDefault(b => Same(b.Name, name));
            if (branch is null)
                errors.Add($"La sucursal {name} no existe o no tiene acceso a ella.");
            return branch;
        }

        /// <summary>El puerto por su nombre ("Gi1/0/12") o su número ("12").</summary>
        public PortRow? Port(string? deviceName, string? port, List<string> errors)
        {
            if (deviceName is null || port is null)
            {
                errors.Add("Indique el dispositivo y el puerto de cada extremo.");
                return null;
            }
            var device = Devices.FirstOrDefault(d => Same(d.Name, deviceName));
            if (device is null)
            {
                errors.Add($"El dispositivo {deviceName} no existe o no tiene acceso a él.");
                return null;
            }
            var mine = Ports.Where(p => p.DeviceId == device.Id).ToList();
            var found = mine.FirstOrDefault(p => Same(p.Name, port))
                ?? (int.TryParse(port, out var number) ? mine.FirstOrDefault(p => p.Number == number) : null);
            if (found is null)
                errors.Add($"{deviceName} no tiene el puerto {port}.");
            return found;
        }
    }
}
