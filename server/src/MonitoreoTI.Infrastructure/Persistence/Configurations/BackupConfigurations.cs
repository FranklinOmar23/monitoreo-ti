using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Backups;
using MonitoreoTI.Domain.Entities.Inventory;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class BackupJobConfiguration : IEntityTypeConfiguration<BackupJob>
{
    public void Configure(EntityTypeBuilder<BackupJob> b)
    {
        b.ToTable("respaldo_trabajos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Source).HasColumnName("origen").Varchar(10);
        b.Property(x => x.SqlInstance).HasColumnName("instancia_sql").HasMaxLength(128);
        b.Property(x => x.Items).HasColumnName("elementos");
        b.Property(x => x.Mode).HasColumnName("modo").Varchar(8);
        b.Property(x => x.StopServices).HasColumnName("servicios_detener");
        b.Property(x => x.ColdMaxMinutes).HasColumnName("frio_max_minutos");
        b.Property(x => x.Strategy).HasColumnName("estrategia").Varchar(12);
        b.Property(x => x.FullEveryDays).HasColumnName("completo_cada_dias");
        b.Property(x => x.DestinationType).HasColumnName("destino_tipo").Varchar(14);
        b.Property(x => x.DestinationPath).HasColumnName("destino_ruta").HasMaxLength(400);
        b.Property(x => x.Disks).HasColumnName("discos");
        b.Property(x => x.EjectAfter).HasColumnName("expulsar");
        b.Property(x => x.NetworkUser).HasColumnName("red_usuario").HasMaxLength(150);
        b.Property(x => x.NetworkEncryptedPassword).HasColumnName("red_clave_cifrada");
        b.Property(x => x.Frequency).HasColumnName("frecuencia").Varchar(8);
        b.Property(x => x.WeekDays).HasColumnName("dias_semana").Varchar(20);
        b.Property(x => x.Time).HasColumnName("hora").Varchar(5);
        b.Property(x => x.KeepDaily).HasColumnName("retener_diarios");
        b.Property(x => x.KeepMonthly).HasColumnName("retener_mensuales");
        b.Property(x => x.Verify).HasColumnName("verificar");
        b.Property(x => x.IsActive).HasColumnName("activo");
        b.Property(x => x.CreatedById).HasColumnName("creado_por");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
        b.Property(x => x.LastRunAt).HasColumnName("ultima_ejecucion");
        b.Property(x => x.LastStatus).HasColumnName("ultimo_estado").Varchar(12);
        b.Property(x => x.LastSuccessAt).HasColumnName("ultimo_correcto");
        b.Property(x => x.LastError).HasColumnName("ultimo_error").HasMaxLength(400);
        b.Property(x => x.NextRunAt).HasColumnName("proxima_ejecucion");
        b.Property(x => x.DiskMissingSince).HasColumnName("sin_disco_desde");
        b.Property(x => x.ServicesNotRestarted).HasColumnName("servicios_sin_arrancar").HasMaxLength(400);

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class BackupRunConfiguration : IEntityTypeConfiguration<BackupRun>
{
    public void Configure(EntityTypeBuilder<BackupRun> b)
    {
        b.ToTable("respaldo_ejecuciones");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.JobId).HasColumnName("trabajo_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.RunUid).HasColumnName("ejecucion_uid");
        b.Property(x => x.Trigger).HasColumnName("disparo").Varchar(10);
        b.Property(x => x.RequestedById).HasColumnName("pedido_por");
        b.Property(x => x.Status).HasColumnName("estado").Varchar(12);
        b.Property(x => x.Kind).HasColumnName("tipo").Varchar(12);
        b.Property(x => x.StartedAt).HasColumnName("inicio");
        b.Property(x => x.FinishedAt).HasColumnName("fin");
        b.Property(x => x.BytesCopied).HasColumnName("bytes_copiados");
        b.Property(x => x.BytesTotal).HasColumnName("bytes_totales");
        b.Property(x => x.Files).HasColumnName("archivos");
        b.Property(x => x.Destination).HasColumnName("destino").HasMaxLength(300);
        b.Property(x => x.SetName).HasColumnName("conjunto").HasMaxLength(200);
        b.Property(x => x.Detail).HasColumnName("detalle").HasMaxLength(1000);

        b.HasOne<BackupJob>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.JobId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
