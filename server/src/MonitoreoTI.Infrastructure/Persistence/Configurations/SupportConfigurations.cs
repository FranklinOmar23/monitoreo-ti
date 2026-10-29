using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Entities.Support;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public void Configure(EntityTypeBuilder<Ticket> b)
    {
        b.ToTable("tickets");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.DeviceId).HasColumnName("dispositivo_id");
        b.Property(x => x.AlertId).HasColumnName("alerta_id");
        b.Property(x => x.CreatedById).HasColumnName("creado_por");
        b.Property(x => x.AssignedToId).HasColumnName("asignado_a");
        b.Property(x => x.Title).HasColumnName("titulo").HasMaxLength(200);
        b.Property(x => x.Description).HasColumnName("descripcion");
        b.Property(x => x.Priority).HasColumnName("prioridad");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
        b.Property(x => x.ClosedAt).HasColumnName("cerrado");

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<NetworkDevice>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.DeviceId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Alert>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.AlertId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.AssignedToId).OnDelete(DeleteBehavior.NoAction);
        b.HasMany(x => x.Comments).WithOne()
            .HasForeignKey(x => new { x.CompanyId, x.TicketId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Attachments).WithOne()
            .HasForeignKey(x => new { x.CompanyId, x.TicketId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> b)
    {
        b.ToTable("ticket_comentarios");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.TicketId).HasColumnName("ticket_id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.Text).HasColumnName("texto");
        b.Property(x => x.CreatedAt).HasColumnName("fecha").DefaultUtcNow();

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
{
    public void Configure(EntityTypeBuilder<TicketAttachment> b)
    {
        b.ToTable("ticket_adjuntos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.TicketId).HasColumnName("ticket_id");
        b.Property(x => x.FileUrl).HasColumnName("archivo_url").HasMaxLength(500);
        b.Property(x => x.ContentType).HasColumnName("tipo").Varchar(50);
        b.Property(x => x.CreatedAt).HasColumnName("fecha").DefaultUtcNow();
    }
}

internal sealed class MaintenanceConfiguration : IEntityTypeConfiguration<Maintenance>
{
    public void Configure(EntityTypeBuilder<Maintenance> b)
    {
        b.ToTable("mantenimientos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.TechnicianId).HasColumnName("tecnico_id");
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.Description).HasColumnName("descripcion").HasMaxLength(500);
        b.Property(x => x.ScheduledDate).HasColumnName("fecha_programada");
        b.Property(x => x.CompletedDate).HasColumnName("fecha_realizada");

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.TechnicianId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ScriptConfiguration : IEntityTypeConfiguration<Script>
{
    public void Configure(EntityTypeBuilder<Script> b)
    {
        b.ToTable("scripts");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.CreatedById).HasColumnName("creado_por");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(150);
        b.Property(x => x.Language).HasColumnName("lenguaje");
        b.Property(x => x.Content).HasColumnName("contenido");
        b.Property(x => x.Signature).HasColumnName("firma").HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CommandConfiguration : IEntityTypeConfiguration<Command>
{
    public void Configure(EntityTypeBuilder<Command> b)
    {
        b.ToTable("comandos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.ScriptId).HasColumnName("script_id");
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.Content).HasColumnName("contenido");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.Result).HasColumnName("resultado");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
        b.Property(x => x.CompletedAt).HasColumnName("terminado");

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        // FK simple: ON DELETE SET NULL no puede ser compuesta porque anularía empresa_id.
        b.HasOne<Script>().WithMany().HasForeignKey(x => x.ScriptId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class RemoteSessionConfiguration : IEntityTypeConfiguration<RemoteSession>
{
    public void Configure(EntityTypeBuilder<RemoteSession> b)
    {
        b.ToTable("sesiones_remotas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.StartedAt).HasColumnName("inicio").DefaultUtcNow();
        b.Property(x => x.EndedAt).HasColumnName("fin");
        b.Property(x => x.SourceIp).HasColumnName("ip_origen").Varchar(45);

        b.HasOne<Machine>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}
