using System.Runtime.Serialization;

namespace MonitoreoTI.Domain.Enums;

public enum DeviceType
{
    [EnumMember(Value = "switch")] Switch,
    [EnumMember(Value = "router")] Router,
    [EnumMember(Value = "firewall")] Firewall,
    [EnumMember(Value = "servidor")] Server,
    [EnumMember(Value = "patch_panel")] PatchPanel,
    [EnumMember(Value = "ups")] Ups,
    [EnumMember(Value = "pdu")] Pdu,
    [EnumMember(Value = "bandeja")] Shelf,
    [EnumMember(Value = "organizador")] CableManager,
    [EnumMember(Value = "access_point")] AccessPoint,
    [EnumMember(Value = "otro")] Other,
}

public enum NetworkDeviceStatus
{
    [EnumMember(Value = "en_linea")] Online,
    [EnumMember(Value = "advertencia")] Warning,
    [EnumMember(Value = "critico")] Critical,
    [EnumMember(Value = "sin_respuesta")] NotResponding,
    [EnumMember(Value = "pasivo")] Passive,
    [EnumMember(Value = "baja")] Retired,
}

public enum PortType
{
    [EnumMember(Value = "rj45")] Rj45,
    [EnumMember(Value = "sfp")] Sfp,
    [EnumMember(Value = "sfp_plus")] SfpPlus,
    [EnumMember(Value = "fibra")] Fiber,
    [EnumMember(Value = "consola")] Console,
    [EnumMember(Value = "otro")] Other,
}

public enum DuplexMode
{
    [EnumMember(Value = "full")] Full,
    [EnumMember(Value = "half")] Half,
    [EnumMember(Value = "auto")] Auto,
}

/// <summary>Colores: verde conectado, gris libre, amarillo con errores, rojo caído.</summary>
public enum PortStatus
{
    [EnumMember(Value = "conectado")] Connected,
    [EnumMember(Value = "libre")] Free,
    [EnumMember(Value = "errores")] Errors,
    [EnumMember(Value = "caido")] Down,
    [EnumMember(Value = "deshabilitado")] Disabled,
}

public enum LinkProtocol
{
    [EnumMember(Value = "lldp")] Lldp,
    [EnumMember(Value = "cdp")] Cdp,
    [EnumMember(Value = "manual")] Manual,
}

public enum CableType
{
    [EnumMember(Value = "cat5e")] Cat5e,
    [EnumMember(Value = "cat6")] Cat6,
    [EnumMember(Value = "cat6a")] Cat6a,
    [EnumMember(Value = "cat7")] Cat7,
    [EnumMember(Value = "fibra_mm")] FiberMultiMode,
    [EnumMember(Value = "fibra_sm")] FiberSingleMode,
    [EnumMember(Value = "dac")] Dac,
    [EnumMember(Value = "otro")] Other,
}

/// <summary>Cómo se vio una dirección IP la última vez.</summary>
public enum IpSource
{
    [EnumMember(Value = "ping")] Ping,
    [EnumMember(Value = "arp")] Arp,

    /// <summary>La reporta el agente del equipo (solo en historial_ip).</summary>
    [EnumMember(Value = "agente")] Agent,
}
