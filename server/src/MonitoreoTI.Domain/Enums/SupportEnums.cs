using System.Runtime.Serialization;

namespace MonitoreoTI.Domain.Enums;

public enum TicketPriority
{
    [EnumMember(Value = "baja")] Low,
    [EnumMember(Value = "media")] Medium,
    [EnumMember(Value = "alta")] High,
    [EnumMember(Value = "urgente")] Urgent,
}

public enum TicketStatus
{
    [EnumMember(Value = "abierto")] Open,
    [EnumMember(Value = "en_proceso")] InProgress,
    [EnumMember(Value = "en_espera")] OnHold,
    [EnumMember(Value = "resuelto")] Resolved,
    [EnumMember(Value = "cerrado")] Closed,
}

public enum MaintenanceType
{
    [EnumMember(Value = "preventivo")] Preventive,
    [EnumMember(Value = "correctivo")] Corrective,
}

public enum ScriptLanguage
{
    [EnumMember(Value = "powershell")] PowerShell,
    [EnumMember(Value = "cmd")] Cmd,
    [EnumMember(Value = "bash")] Bash,
    [EnumMember(Value = "python")] Python,
}

public enum CommandType
{
    [EnumMember(Value = "comando")] Command,
    [EnumMember(Value = "script")] Script,
    [EnumMember(Value = "accion")] Action,
}

public enum CommandStatus
{
    [EnumMember(Value = "pendiente")] Pending,
    [EnumMember(Value = "enviado")] Sent,
    [EnumMember(Value = "ejecutando")] Running,
    [EnumMember(Value = "completado")] Completed,
    [EnumMember(Value = "error")] Failed,
    [EnumMember(Value = "expirado")] Expired,
}

public enum RemoteSessionType
{
    [EnumMember(Value = "escritorio")] Desktop,
    [EnumMember(Value = "terminal")] Terminal,
    [EnumMember(Value = "archivos")] Files,
}
