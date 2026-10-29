using System.Runtime.Serialization;

namespace MonitoreoTI.Domain.Enums;

public enum MachineType
{
    [EnumMember(Value = "computadora")] Desktop,
    [EnumMember(Value = "laptop")] Laptop,
    [EnumMember(Value = "servidor")] Server,
    [EnumMember(Value = "impresora")] Printer,
    [EnumMember(Value = "otro")] Other,
}

/// <summary>Semáforo: verde sano, amarillo advertencia, rojo crítico, gris desconectado.</summary>
public enum MachineStatus
{
    [EnumMember(Value = "sano")] Healthy,
    [EnumMember(Value = "advertencia")] Warning,
    [EnumMember(Value = "critico")] Critical,
    [EnumMember(Value = "desconectado")] Disconnected,
    [EnumMember(Value = "baja")] Retired,
}

public enum HealthStatus
{
    [EnumMember(Value = "sano")] Healthy,
    [EnumMember(Value = "advertencia")] Warning,
    [EnumMember(Value = "critico")] Critical,
    [EnumMember(Value = "desconocido")] Unknown,
}

public enum NetworkInterfaceType
{
    [EnumMember(Value = "cableada")] Wired,
    [EnumMember(Value = "wifi")] Wifi,
    [EnumMember(Value = "virtual")] Virtual,
    [EnumMember(Value = "otra")] Other,
}
