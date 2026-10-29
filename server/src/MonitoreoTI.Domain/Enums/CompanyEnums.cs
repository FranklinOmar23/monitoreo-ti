using System.Runtime.Serialization;

namespace MonitoreoTI.Domain.Enums;

// [EnumMember] guarda el valor exacto de la columna en la base de datos (CHECK del script).

public enum CompanyStatus
{
    [EnumMember(Value = "activa")] Active,
    [EnumMember(Value = "suspendida")] Suspended,
    [EnumMember(Value = "cancelada")] Cancelled,
}

public enum SubscriptionStatus
{
    [EnumMember(Value = "activa")] Active,
    [EnumMember(Value = "vencida")] Expired,
    [EnumMember(Value = "cancelada")] Cancelled,
}

public enum ModuleKey
{
    [EnumMember(Value = "monitoreo")] Monitoring,
    [EnumMember(Value = "remoto")] Remote,
    [EnumMember(Value = "red")] Network,
    [EnumMember(Value = "reportes_avanzados")] AdvancedReports,
}
