using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Security;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

// Las relaciones entre tablas de negocio usan la clave (empresa_id, id), igual que las FK compuestas del script.

internal sealed class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> b)
    {
        b.ToTable("sucursales");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Province).HasColumnName("provincia").HasMaxLength(100);
        b.Property(x => x.Address).HasColumnName("direccion").HasMaxLength(300);
        b.Property(x => x.ManagerId).HasColumnName("responsable_id");
        b.Property(x => x.IsActive).HasColumnName("activa");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.ManagerId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UserBranchConfiguration : IEntityTypeConfiguration<UserBranch>
{
    public void Configure(EntityTypeBuilder<UserBranch> b)
    {
        b.ToTable("usuario_sucursal");
        b.HasKey(x => new { x.UserId, x.BranchId });
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(x => x.Branch).WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departamentos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.ResponsibleUserId).HasColumnName("responsable_id");

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> b)
    {
        b.ToTable("maquinas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.DepartmentId).HasColumnName("departamento_id");
        b.Property(x => x.QrCode).HasColumnName("codigo_qr").DefaultNewGuid();
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.TypeLocked).HasColumnName("tipo_fijo");
        b.Property(x => x.Brand).HasColumnName("marca").HasMaxLength(100);
        b.Property(x => x.Model).HasColumnName("modelo").HasMaxLength(100);
        b.Property(x => x.SerialNumber).HasColumnName("serie").HasMaxLength(100);
        b.Property(x => x.OperatingSystem).HasColumnName("sistema_operativo").HasMaxLength(150);
        b.Property(x => x.IpAddress).HasColumnName("ip").Varchar(45);
        b.Property(x => x.MacAddress).HasColumnName("mac").Varchar(17);
        b.Property(x => x.AssignedTo).HasColumnName("responsable").HasMaxLength(150);
        b.Property(x => x.PurchaseDate).HasColumnName("fecha_compra");
        b.Property(x => x.WarrantyUntil).HasColumnName("garantia_hasta");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.LastReportAt).HasColumnName("ultimo_reporte");
        b.Property(x => x.RamTotalMb).HasColumnName("ram_total_mb");
        b.Property(x => x.ProcessCount).HasColumnName("procesos_total");
        b.Property(x => x.SnmpVersion).HasColumnName("snmp_version").HasMaxLength(3).IsUnicode(false);
        b.Property(x => x.SnmpUser).HasColumnName("snmp_usuario").HasMaxLength(100);
        b.Property(x => x.SnmpEncryptedCredential).HasColumnName("snmp_credencial_cifrada");
        b.Property(x => x.CollectorAgentId).HasColumnName("recolector_id");
        b.Property(x => x.LastSnmpPollAt).HasColumnName("ultimo_sondeo_snmp");
        b.Property(x => x.LastSnmpError).HasColumnName("ultimo_error_snmp").HasMaxLength(300);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });

        b.HasOne(x => x.Branch).WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Department).WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.DepartmentId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Agent).WithOne()
            .HasForeignKey<Agent>(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey<Machine>(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Components).WithOne()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Software).WithOne()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.NetworkInterfaces).WithOne()
            .HasForeignKey(x => new { x.CompanyId, x.MachineId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ComponentConfiguration : IEntityTypeConfiguration<Component>
{
    public void Configure(EntityTypeBuilder<Component> b)
    {
        b.ToTable("componentes");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Type).HasColumnName("tipo").Varchar(30);
        b.Property(x => x.Description).HasColumnName("descripcion").HasMaxLength(200);
        b.Property(x => x.Capacity).HasColumnName("capacidad").HasMaxLength(50);
        b.Property(x => x.HealthStatus).HasColumnName("estado_salud");
        b.Property(x => x.Detail).HasColumnName("detalle").HasMaxLength(200);
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
    }
}

internal sealed class InstalledSoftwareConfiguration : IEntityTypeConfiguration<InstalledSoftware>
{
    public void Configure(EntityTypeBuilder<InstalledSoftware> b)
    {
        b.ToTable("software_instalado");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(250);
        b.Property(x => x.Version).HasColumnName("version").HasMaxLength(100);
        b.Property(x => x.Publisher).HasColumnName("editor").HasMaxLength(200);
        b.Property(x => x.InstalledOn).HasColumnName("fecha_instalacion");
    }
}

internal sealed class NetworkInterfaceConfiguration : IEntityTypeConfiguration<NetworkInterface>
{
    public void Configure(EntityTypeBuilder<NetworkInterface> b)
    {
        b.ToTable("interfaces_red");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(150);
        b.Property(x => x.MacAddress).HasColumnName("mac").Varchar(17);
        b.Property(x => x.IpAddress).HasColumnName("ip").Varchar(45);
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.SpeedMbps).HasColumnName("velocidad_mbps");
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
    }
}

internal sealed class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> b)
    {
        b.ToTable("agentes");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Version).HasColumnName("version").Varchar(30);
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(255);
        b.Property(x => x.IsCollector).HasColumnName("es_recolector");
        b.Property(x => x.LastContactAt).HasColumnName("ultimo_contacto");
        b.Property(x => x.IsRevoked).HasColumnName("revocado");
        // Sin HasDefaultValue: con el centinela de EF, un false explícito se perdería y la base pondría su DEFAULT 1.
        b.Property(x => x.IsApproved).HasColumnName("aprobado");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
    }
}

internal sealed class EnrollmentTokenConfiguration : IEntityTypeConfiguration<EnrollmentToken>
{
    public void Configure(EntityTypeBuilder<EnrollmentToken> b)
    {
        b.ToTable("tokens_registro");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(255);
        b.Property(x => x.EncryptedToken).HasColumnName("token_cifrado");
        b.Property(x => x.ExpiresAt).HasColumnName("vence");
        b.Property(x => x.MaxUses).HasColumnName("usos_max");
        b.Property(x => x.Uses).HasColumnName("usos");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.RequiresApproval).HasColumnName("requiere_aprobacion");
        b.Property(x => x.RevokedAt).HasColumnName("revocado");
        b.Property(x => x.CreatedById).HasColumnName("creado_por");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VolumeConfiguration : IEntityTypeConfiguration<Volume>
{
    public void Configure(EntityTypeBuilder<Volume> b)
    {
        b.ToTable("volumenes");
        b.HasKey(x => new { x.CompanyId, x.MachineId, x.Drive });
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Drive).HasColumnName("unidad").Varchar(10);
        b.Property(x => x.Label).HasColumnName("etiqueta").HasMaxLength(100);
        b.Property(x => x.TotalGb).HasColumnName("total_gb").HasPrecision(10, 1);
        b.Property(x => x.FreeGb).HasColumnName("libre_gb").HasPrecision(10, 1);
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
    }
}

internal sealed class WindowsServiceConfiguration : IEntityTypeConfiguration<WindowsService>
{
    public void Configure(EntityTypeBuilder<WindowsService> b)
    {
        b.ToTable("servicios");
        b.HasKey(x => new { x.CompanyId, x.MachineId, x.Name });
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(256);
        b.Property(x => x.DisplayName).HasColumnName("nombre_visible").HasMaxLength(256);
        b.Property(x => x.State).HasColumnName("estado").Varchar(20);
        b.Property(x => x.StartMode).HasColumnName("inicio").Varchar(25);
        b.Property(x => x.StoppedSince).HasColumnName("detenido_desde");
        b.Property(x => x.UpdatedAt).HasColumnName("actualizado").DefaultUtcNow();
    }
}
