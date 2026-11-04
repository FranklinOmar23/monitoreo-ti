using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>
/// Equipo monitoreado. Tabla: maquinas.
/// Nunca se borra: se da de baja con Status = Retired para conservar el historial.
/// </summary>
public class Machine : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public int? DepartmentId { get; set; }

    /// <summary>Código de la etiqueta QR; lo genera la base al insertar.</summary>
    public Guid QrCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public MachineType Type { get; set; } = MachineType.Desktop;
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? OperatingSystem { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>MAC principal, formato AA:BB:CC:DD:EE:FF.</summary>
    public string? MacAddress { get; set; }
    public string? AssignedTo { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? WarrantyUntil { get; set; }
    public MachineStatus Status { get; set; } = MachineStatus.Disconnected;
    public DateTime? LastReportAt { get; set; }

    /// <summary>Memoria instalada (del inventario de hardware), para mostrar "usado / total".</summary>
    public int? RamTotalMb { get; set; }

    /// <summary>Procesos en ejecución en la última muestra.</summary>
    public int? ProcessCount { get; set; }

    /// <summary>Impresoras: v2c o v3; el recolector de su sucursal lee sus consumibles por SNMP.</summary>
    public string? SnmpVersion { get; set; }
    public string? SnmpUser { get; set; }

    /// <summary>Comunidad v2c o claves v3, cifradas por la aplicación; nunca en texto plano.</summary>
    public byte[]? SnmpEncryptedCredential { get; set; }
    public int? CollectorAgentId { get; set; }
    public DateTime? LastSnmpPollAt { get; set; }
    public string? LastSnmpError { get; set; }
    public DateTime CreatedAt { get; set; }

    public Branch? Branch { get; set; }
    public Department? Department { get; set; }
    public Agent? Agent { get; set; }
    public ICollection<Component> Components { get; set; } = new List<Component>();
    public ICollection<InstalledSoftware> Software { get; set; } = new List<InstalledSoftware>();
    public ICollection<NetworkInterface> NetworkInterfaces { get; set; } = new List<NetworkInterface>();
}
