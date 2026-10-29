namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>Permiso del sistema, ej. 'remoto.conectar'. Tabla global: permisos.</summary>
public class Permission
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
}
