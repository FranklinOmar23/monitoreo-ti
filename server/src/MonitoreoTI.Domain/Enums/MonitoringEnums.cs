using System.Runtime.Serialization;

namespace MonitoreoTI.Domain.Enums;

public enum EventLevel
{
    [EnumMember(Value = "info")] Info,
    [EnumMember(Value = "advertencia")] Warning,
    [EnumMember(Value = "error")] Error,
    [EnumMember(Value = "critico")] Critical,
}

public enum AlertMetric
{
    [EnumMember(Value = "cpu")] Cpu,
    [EnumMember(Value = "ram")] Ram,
    [EnumMember(Value = "disco")] Disk,
    [EnumMember(Value = "temperatura")] Temperature,
    [EnumMember(Value = "sin_reportar")] NotReporting,
    [EnumMember(Value = "errores_criticos")] CriticalErrors,
    [EnumMember(Value = "servicio_detenido")] ServiceStopped,
    [EnumMember(Value = "toner")] Toner,
    [EnumMember(Value = "antivirus")] Antivirus,
    [EnumMember(Value = "puerto_errores")] PortErrors,
    [EnumMember(Value = "puerto_caido")] PortDown,
    [EnumMember(Value = "switch_sin_respuesta")] SwitchNotResponding,
}

public enum ComparisonOperator
{
    [EnumMember(Value = ">")] GreaterThan,
    [EnumMember(Value = ">=")] GreaterThanOrEqual,
    [EnumMember(Value = "<")] LessThan,
    [EnumMember(Value = "<=")] LessThanOrEqual,
    [EnumMember(Value = "=")] Equal,
}

public enum AlertLevel
{
    [EnumMember(Value = "advertencia")] Warning,
    [EnumMember(Value = "critico")] Critical,
}

public enum AlertStatus
{
    [EnumMember(Value = "nueva")] New,
    [EnumMember(Value = "en_proceso")] InProgress,
    [EnumMember(Value = "resuelta")] Resolved,
}
