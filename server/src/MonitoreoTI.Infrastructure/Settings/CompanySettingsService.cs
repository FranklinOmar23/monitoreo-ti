using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Settings;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Infrastructure.Settings;

/// <summary>
/// Configuración de la empresa (W-40): sus datos y el uso del plan, la apariencia del panel (logo y color),
/// los departamentos y los ajustes de seguridad. Todo cambio queda en la auditoría con el antes y el después.
/// </summary>
public sealed class CompanySettingsService(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit)
{
    public const int MaxLogoBytes = 256 * 1024;

    // ---------------------------------------------------------------- empresa y plan

    public async Task<CompanySettings> GetCompanyAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var company = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new
            {
                c.Name,
                c.TaxId,
                c.ContactEmail,
                c.Phone,
                Plan = c.Plan!.Name,
                c.Plan.MaxAgents,
                c.Plan.MaxBranches,
                c.Plan.MaxUsers,
                Modules = c.Plan.Modules.Select(m => m.Module).ToList(),
            })
            .FirstAsync(ct);

        var agents = await db.Agents.CountAsync(a => !a.IsRevoked, ct);
        var branches = await db.Branches.CountAsync(ct);
        var users = await db.UserCompanies.CountAsync(m => m.CompanyId == companyId && m.IsActive
            && db.Users.Any(u => u.Id == m.UserId && u.IsActive), ct);

        return new CompanySettings(
            company.Name, company.TaxId, company.ContactEmail, company.Phone, company.Plan,
            new PlanUsage(agents, company.MaxAgents), new PlanUsage(branches, company.MaxBranches), new PlanUsage(users, company.MaxUsers),
            company.Modules.Select(EnumMemberConverter<ModuleKey>.ToProvider).OrderBy(m => m).ToList());
    }

    public async Task<CommandResult> UpdateCompanyAsync(CompanyInput input, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        var name = input.Nombre.Trim();
        // empresas.nombre es único en todo el sistema.
        if (await db.Companies.AnyAsync(c => c.Id != companyId && c.Name == name, ct))
            return CommandResult.Conflict("Ya hay otra empresa con ese nombre.");

        var before = new { nombre = company.Name, rnc = company.TaxId, correo = company.ContactEmail, telefono = company.Phone };
        company.Name = name;
        company.TaxId = Blank(input.Rnc);
        company.ContactEmail = Blank(input.CorreoContacto);
        company.Phone = Blank(input.Telefono);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("configuracion.empresa", "empresas", companyId.ToString(), before,
            new { nombre = company.Name, rnc = company.TaxId, correo = company.ContactEmail, telefono = company.Phone }, ct);
        return CommandResult.Ok(companyId);
    }

    // ---------------------------------------------------------------- apariencia

    /// <summary>Lo que pinta el panel de cualquier usuario de la empresa (no exige permisos).</summary>
    public async Task<AppearanceData> AppearanceAsync(int companyId, CancellationToken ct)
    {
        var row = await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.BrandColor, c.Logo, c.LogoType })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return new AppearanceData(null, null);
        var logo = row.Logo is { Length: > 0 } bytes && row.LogoType is not null
            ? $"data:{row.LogoType};base64,{Convert.ToBase64String(bytes)}"
            : null;
        return new AppearanceData(row.BrandColor, logo);
    }

    public async Task<CommandResult> SetColorAsync(string? color, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        var before = company.BrandColor;
        company.BrandColor = color?.ToLowerInvariant();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("configuracion.color", "empresas", companyId.ToString(), new { color = before }, new { color = company.BrandColor }, ct);
        return CommandResult.Ok(companyId);
    }

    public async Task<CommandResult> SetLogoAsync(string base64, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return CommandResult.Invalid("El archivo no llegó bien. Vuelva a elegirlo.");
        }
        if (bytes.Length == 0)
            return CommandResult.Invalid("El archivo está vacío.");
        if (bytes.Length > MaxLogoBytes)
            return CommandResult.Invalid("El logo pesa más de 256 KB. Use uno más chico.");
        if (ImageType(bytes) is not { } type)
            return CommandResult.Invalid("El logo debe ser una imagen PNG, JPEG o WebP.");

        var companyId = user.CompanyId;
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        var before = Describe(company.Logo, company.LogoType);
        company.Logo = bytes;
        company.LogoType = type;
        await db.SaveChangesAsync(ct);
        // En la auditoría va solo el tipo y el tamaño, no la imagen.
        await audit.RecordAsync("configuracion.logo", "empresas", companyId.ToString(), before, Describe(bytes, type), ct);
        return CommandResult.Ok(companyId);
    }

    public async Task<CommandResult> RemoveLogoAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        if (company.Logo is null)
            return CommandResult.Ok(companyId);
        var before = Describe(company.Logo, company.LogoType);
        company.Logo = null;
        company.LogoType = null;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("configuracion.logo", "empresas", companyId.ToString(), before, null, ct);
        return CommandResult.Ok(companyId);
    }

    /// <summary>El tipo según los primeros bytes (la extensión o lo que diga el panel no cuentan).</summary>
    public static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    private static object? Describe(byte[]? logo, string? type) =>
        logo is null ? null : new { tipo = type, kb = (logo.Length + 1023) / 1024 };

    // ---------------------------------------------------------------- seguridad

    public async Task<SecuritySettings> SecurityAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        return await db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new SecuritySettings(c.AskRemoteConsent, c.AutoUpdateAgents))
            .FirstAsync(ct);
    }

    public async Task<SecuritySettings> SetSecurityAsync(SecuritySettings input, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var company = await db.Companies.FirstAsync(c => c.Id == companyId, ct);
        // Las mismas acciones de auditoría que en Acceso remoto e Instaladores.
        if (company.AskRemoteConsent != input.PedirPermiso)
        {
            company.AskRemoteConsent = input.PedirPermiso;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("remoto.configuracion", "empresas", companyId.ToString(), null, new { pedirPermiso = input.PedirPermiso }, ct);
        }
        if (company.AutoUpdateAgents != input.ActualizacionAutomatica)
        {
            company.AutoUpdateAgents = input.ActualizacionAutomatica;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("actualizacion.automatica", "empresas", companyId.ToString(), null, new { activa = input.ActualizacionAutomatica }, ct);
        }
        return new SecuritySettings(company.AskRemoteConsent, company.AutoUpdateAgents);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
