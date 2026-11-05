using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Entities.Security;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class MetricConfiguration : IEntityTypeConfiguration<Metric>
{
    public void Configure(EntityTypeBuilder<Metric> b)
    {
        b.ToTable("metricas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Timestamp).HasColumnName("fecha_hora").DefaultUtcNow();
        b.Property(x => x.Cpu).HasColumnName("cpu").HasPrecision(5, 2);
        b.Property(x => x.Ram).HasColumnName("ram").HasPrecision(5, 2);
        b.Property(x => x.Disk).HasColumnName("disco").HasPrecision(5, 2);
        b.Property(x => x.Temperature).HasColumnName("temperatura").HasPrecision(5, 2);

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class HourlyMetricConfiguration : IEntityTypeConfiguration<HourlyMetric>
{
    public void Configure(EntityTypeBuilder<HourlyMetric> b)
    {
        b.ToTable("metricas_hora");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Hour).HasColumnName("hora");
        b.Property(x => x.CpuAverage).HasColumnName("cpu_promedio").HasPrecision(5, 2);
        b.Property(x => x.RamAverage).HasColumnName("ram_promedio").HasPrecision(5, 2);
        b.Property(x => x.DiskMax).HasColumnName("disco_max").HasPrecision(5, 2);
        b.Property(x => x.TemperatureMax).HasColumnName("temperatura_max").HasPrecision(5, 2);

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ErrorEventConfiguration : IEntityTypeConfiguration<ErrorEvent>
{
    public void Configure(EntityTypeBuilder<ErrorEvent> b)
    {
        b.ToTable("eventos_error");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Timestamp).HasColumnName("fecha_hora");
        b.Property(x => x.Source).HasColumnName("origen").HasMaxLength(200);
        b.Property(x => x.Code).HasColumnName("codigo");
        b.Property(x => x.Level).HasColumnName("nivel");
        b.Property(x => x.Message).HasColumnName("mensaje");
        b.Property(x => x.SimpleMessage).HasColumnName("mensaje_simple").HasMaxLength(500);

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ProcessSnapshotConfiguration : IEntityTypeConfiguration<ProcessSnapshot>
{
    public void Configure(EntityTypeBuilder<ProcessSnapshot> b)
    {
        b.ToTable("procesos_snapshot");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Timestamp).HasColumnName("fecha_hora").DefaultUtcNow();
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(260);
        b.Property(x => x.Pid).HasColumnName("pid");
        b.Property(x => x.Cpu).HasColumnName("cpu").HasPrecision(5, 2);
        b.Property(x => x.RamMb).HasColumnName("ram_mb").HasPrecision(10, 2);

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AlertRuleConfiguration : IEntityTypeConfiguration<AlertRule>
{
    public void Configure(EntityTypeBuilder<AlertRule> b)
    {
        b.ToTable("reglas_alerta");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Metric).HasColumnName("metrica");
        b.Property(x => x.Operator).HasColumnName("operador");
        b.Property(x => x.WarningThreshold).HasColumnName("umbral_advertencia").HasPrecision(10, 2);
        b.Property(x => x.CriticalThreshold).HasColumnName("umbral_critico").HasPrecision(10, 2);
        b.Property(x => x.DurationMinutes).HasColumnName("duracion_min");
        b.Property(x => x.IsActive).HasColumnName("activa");
        b.Property(x => x.AutoRepair).HasColumnName("reparar_auto");
        b.Property(x => x.AutoRepairUserId).HasColumnName("reparar_usuario_id");

        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class AlertConfiguration : IEntityTypeConfiguration<Alert>
{
    public void Configure(EntityTypeBuilder<Alert> b)
    {
        b.ToTable("alertas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.DeviceId).HasColumnName("dispositivo_id");
        b.Property(x => x.PortId).HasColumnName("puerto_id");
        b.Property(x => x.RuleId).HasColumnName("regla_id");
        b.Property(x => x.HandledById).HasColumnName("atendida_por");
        b.Property(x => x.Level).HasColumnName("nivel");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.Message).HasColumnName("mensaje").HasMaxLength(500);
        b.Property(x => x.Code).HasColumnName("codigo").Varchar(80);
        b.Property(x => x.Detail).HasColumnName("detalle").HasMaxLength(500);
        b.Property(x => x.Advice).HasColumnName("que_hacer").HasMaxLength(500);
        b.Property(x => x.Source).HasColumnName("origen").HasMaxLength(200);
        b.Property(x => x.CreatedAt).HasColumnName("creada").DefaultUtcNow();
        b.Property(x => x.ResolvedAt).HasColumnName("resuelta");
        b.Property(x => x.NotifiedAt).HasColumnName("notificada");

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<NetworkDevice>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.DeviceId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.PortId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        // FK simple: ON DELETE SET NULL no puede ser compuesta porque anularía empresa_id.
        b.HasOne<AlertRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.HandledById).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DailyFleetStatusConfiguration : IEntityTypeConfiguration<DailyFleetStatus>
{
    public void Configure(EntityTypeBuilder<DailyFleetStatus> b)
    {
        b.ToTable("estado_diario");
        b.HasKey(x => new { x.CompanyId, x.Date });
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Date).HasColumnName("fecha");
        b.Property(x => x.Healthy).HasColumnName("sano");
        b.Property(x => x.Warning).HasColumnName("advertencia");
        b.Property(x => x.Critical).HasColumnName("critico");
        b.Property(x => x.Disconnected).HasColumnName("desconectado");
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
    }
}
