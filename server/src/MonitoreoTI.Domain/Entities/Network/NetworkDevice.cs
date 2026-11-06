using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Equipo de red o de data center (switch, router, patch panel, UPS...). Tabla: dispositivos_red.
/// Nunca se borra: se da de baja con Status = Retired.
/// </summary>
public class NetworkDevice : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>La del gabinete si va en uno; define el alcance por sucursal y qué agente lo consulta.</summary>
    public int BranchId { get; set; }

    /// <summary>null si no va en gabinete (ej. access point).</summary>
    public int? RackId { get; set; }
    public int ModelId { get; set; }

    /// <summary>Agente que lo consulta por SNMP.</summary>
    public int? CollectorAgentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DeviceType Type { get; set; }
    public string? ManagementIp { get; set; }

    /// <summary>U inferior que ocupa en el gabinete.</summary>
    public byte? RackPositionU { get; set; }
    public byte HeightUnits { get; set; } = 1;
    public Guid QrCode { get; set; }

    /// <summary>Usuario SNMPv3 de solo lectura.</summary>
    public string? SnmpUser { get; set; }

    /// <summary>Credencial SNMP cifrada por la aplicación; nunca en texto plano.</summary>
    public byte[]? SnmpEncryptedCredential { get; set; }
    public string? Firmware { get; set; }
    public NetworkDeviceStatus Status { get; set; } = NetworkDeviceStatus.NotResponding;
    public DateTime? LastPolledAt { get; set; }

    /// <summary>Por qué falló el último sondeo SNMP; null si respondió.</summary>
    public string? LastError { get; set; }

    /// <summary>API REST del FortiGate (decisión 76): dirección ("https://192.168.3.3:12443") y token cifrado por la aplicación.</summary>
    public string? ApiUrl { get; set; }

    public byte[]? ApiEncryptedToken { get; set; }

    /// <summary>Aceptar el certificado propio del equipo (los FortiGate traen uno autofirmado).</summary>
    public bool ApiAcceptSelfSigned { get; set; } = true;

    public DateTime? ApiLastOkAt { get; set; }
    public string? ApiLastError { get; set; }

    /// <summary>
    /// FortiSwitch administrado (decisión 80): sus datos llegan por la API de este FortiGate, no por SNMP. Se reconoce
    /// por su número de serie.
    /// </summary>
    public int? FortiGateId { get; set; }

    public string? SerialNumber { get; set; }
    public DateTime CreatedAt { get; set; }

    public Rack? Rack { get; set; }
    public DeviceModel? Model { get; set; }
    public ICollection<Port> Ports { get; set; } = new List<Port>();
}
