using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Infrastructure.Persistence.Views;

/// <summary>
/// Filas de las vistas de los listados (decisión 83): solo lectura, sin llave. Llevan empresa_id para que también les
/// aplique el filtro por empresa de EF (además de RLS en la base).
/// </summary>
public sealed class MachineListRow : ITenantEntity
{
    public int CompanyId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public MachineType Type { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? OperatingSystem { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? AssignedTo { get; set; }
    public Guid QrCode { get; set; }
    public MachineStatus Status { get; set; }
    public DateTime? LastReportAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int BranchId { get; set; }
    public string Branch { get; set; } = "";
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public int StatusOrder { get; set; }
    public DateTime? MetricAt { get; set; }
    public decimal? Cpu { get; set; }
    public decimal? Ram { get; set; }
    public decimal? Disk { get; set; }
    public decimal? Temperature { get; set; }
    public string? AgentVersion { get; set; }
    public int OpenAlerts { get; set; }
}

public sealed class FleetStatusRow : ITenantEntity
{
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public string Branch { get; set; } = "";
    public string? Province { get; set; }
    public bool IsActive { get; set; }
    public int Total { get; set; }
    public int Healthy { get; set; }
    public int Warning { get; set; }
    public int Critical { get; set; }
    public int Disconnected { get; set; }
    public int NewLast7Days { get; set; }
    public int CriticalWithTicket { get; set; }
    public int DisconnectedOver24h { get; set; }
}

public sealed class AlertListRow : ITenantEntity
{
    public int CompanyId { get; set; }
    public long Id { get; set; }
    public AlertLevel Level { get; set; }
    public AlertStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string? Source { get; set; }
    public string? Code { get; set; }
    public int? MachineId { get; set; }
    public string? Machine { get; set; }
    public int? DeviceId { get; set; }
    public string? Device { get; set; }
    public int? BranchId { get; set; }
    public string? Branch { get; set; }
    public int? HandledById { get; set; }
    public string? HandledBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int LevelOrder { get; set; }
    public int IsResolved { get; set; }
    public DateTime SortDate { get; set; }
}

public sealed class TicketListRow : ITenantEntity
{
    public int CompanyId { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public TicketPriority Priority { get; set; }
    public int PriorityOrder { get; set; }
    public TicketStatus Status { get; set; }
    public int? MachineId { get; set; }
    public string? Machine { get; set; }
    public int? BranchId { get; set; }
    public string? Branch { get; set; }
    public long? AlertId { get; set; }
    public int? AssignedToId { get; set; }
    public string? AssignedTo { get; set; }
    public int CreatedById { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public int Comments { get; set; }
}

public sealed class ProblemRow : ITenantEntity
{
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = "";
    public MachineStatus Status { get; set; }
    public int BranchId { get; set; }
    public int Events { get; set; }
    public string? Summary { get; set; }
    public string? Alert { get; set; }
}

internal sealed class MachineListRowConfiguration : IEntityTypeConfiguration<MachineListRow>
{
    public void Configure(EntityTypeBuilder<MachineListRow> b)
    {
        b.HasNoKey().ToView("vw_maquinas_lista");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("nombre");
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.Brand).HasColumnName("marca");
        b.Property(x => x.Model).HasColumnName("modelo");
        b.Property(x => x.SerialNumber).HasColumnName("serie");
        b.Property(x => x.OperatingSystem).HasColumnName("sistema_operativo");
        b.Property(x => x.IpAddress).HasColumnName("ip").IsUnicode(false);
        b.Property(x => x.MacAddress).HasColumnName("mac").IsUnicode(false);
        b.Property(x => x.AssignedTo).HasColumnName("responsable");
        b.Property(x => x.QrCode).HasColumnName("codigo_qr");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.LastReportAt).HasColumnName("ultimo_reporte");
        b.Property(x => x.CreatedAt).HasColumnName("creado");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Branch).HasColumnName("sucursal");
        b.Property(x => x.DepartmentId).HasColumnName("departamento_id");
        b.Property(x => x.Department).HasColumnName("departamento");
        b.Property(x => x.StatusOrder).HasColumnName("orden_estado");
        b.Property(x => x.MetricAt).HasColumnName("metrica_fecha");
        b.Property(x => x.Cpu).HasColumnName("cpu").HasPrecision(5, 2);
        b.Property(x => x.Ram).HasColumnName("ram").HasPrecision(5, 2);
        b.Property(x => x.Disk).HasColumnName("disco").HasPrecision(5, 2);
        b.Property(x => x.Temperature).HasColumnName("temperatura").HasPrecision(5, 2);
        b.Property(x => x.AgentVersion).HasColumnName("version_agente").IsUnicode(false);
        b.Property(x => x.OpenAlerts).HasColumnName("alertas_abiertas");
    }
}

internal sealed class FleetStatusRowConfiguration : IEntityTypeConfiguration<FleetStatusRow>
{
    public void Configure(EntityTypeBuilder<FleetStatusRow> b)
    {
        b.HasNoKey().ToView("vw_estado_flota");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Branch).HasColumnName("sucursal");
        b.Property(x => x.Province).HasColumnName("provincia");
        b.Property(x => x.IsActive).HasColumnName("activa");
        b.Property(x => x.Total).HasColumnName("total");
        b.Property(x => x.Healthy).HasColumnName("sanos");
        b.Property(x => x.Warning).HasColumnName("advertencias");
        b.Property(x => x.Critical).HasColumnName("criticos");
        b.Property(x => x.Disconnected).HasColumnName("desconectados");
        b.Property(x => x.NewLast7Days).HasColumnName("nuevos_7d");
        b.Property(x => x.CriticalWithTicket).HasColumnName("criticos_con_ticket");
        b.Property(x => x.DisconnectedOver24h).HasColumnName("desconectados_24h");
    }
}

internal sealed class AlertListRowConfiguration : IEntityTypeConfiguration<AlertListRow>
{
    public void Configure(EntityTypeBuilder<AlertListRow> b)
    {
        b.HasNoKey().ToView("vw_alertas_lista");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Level).HasColumnName("nivel");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.Message).HasColumnName("mensaje");
        b.Property(x => x.Source).HasColumnName("origen");
        b.Property(x => x.Code).HasColumnName("codigo").IsUnicode(false);
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Machine).HasColumnName("maquina");
        b.Property(x => x.DeviceId).HasColumnName("dispositivo_id");
        b.Property(x => x.Device).HasColumnName("dispositivo");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Branch).HasColumnName("sucursal");
        b.Property(x => x.HandledById).HasColumnName("atendida_por");
        b.Property(x => x.HandledBy).HasColumnName("atendida_por_nombre");
        b.Property(x => x.CreatedAt).HasColumnName("creada");
        b.Property(x => x.ResolvedAt).HasColumnName("resuelta");
        b.Property(x => x.LevelOrder).HasColumnName("orden_nivel");
        b.Property(x => x.IsResolved).HasColumnName("es_resuelta");
        b.Property(x => x.SortDate).HasColumnName("fecha_orden");
    }
}

internal sealed class TicketListRowConfiguration : IEntityTypeConfiguration<TicketListRow>
{
    public void Configure(EntityTypeBuilder<TicketListRow> b)
    {
        b.HasNoKey().ToView("vw_tickets_lista");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Title).HasColumnName("titulo");
        b.Property(x => x.Priority).HasColumnName("prioridad");
        b.Property(x => x.PriorityOrder).HasColumnName("orden_prioridad");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Machine).HasColumnName("maquina");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Branch).HasColumnName("sucursal");
        b.Property(x => x.AlertId).HasColumnName("alerta_id");
        b.Property(x => x.AssignedToId).HasColumnName("asignado_a");
        b.Property(x => x.AssignedTo).HasColumnName("asignado_nombre");
        b.Property(x => x.CreatedById).HasColumnName("creado_por");
        b.Property(x => x.CreatedBy).HasColumnName("creado_por_nombre");
        b.Property(x => x.CreatedAt).HasColumnName("creado");
        b.Property(x => x.ClosedAt).HasColumnName("cerrado");
        b.Property(x => x.Comments).HasColumnName("comentarios");
    }
}

internal sealed class ProblemRowConfiguration : IEntityTypeConfiguration<ProblemRow>
{
    public void Configure(EntityTypeBuilder<ProblemRow> b)
    {
        b.HasNoKey().ToView("vw_problemas_7d");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Name).HasColumnName("nombre");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Events).HasColumnName("eventos");
        b.Property(x => x.Summary).HasColumnName("resumen");
        b.Property(x => x.Alert).HasColumnName("alerta");
    }
}
