using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Network;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.ToTable("salas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Location).HasColumnName("ubicacion").HasMaxLength(200);

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RackConfiguration : IEntityTypeConfiguration<Rack>
{
    public void Configure(EntityTypeBuilder<Rack> b)
    {
        b.ToTable("gabinetes");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.RoomId).HasColumnName("sala_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(50);
        b.Property(x => x.QrCode).HasColumnName("codigo_qr").DefaultNewGuid();
        b.Property(x => x.HeightUnits).HasColumnName("altura_u");
        b.Property(x => x.Row).HasColumnName("fila").HasMaxLength(10);
        b.Property(x => x.Position).HasColumnName("posicion");
        b.Property(x => x.IsActive).HasColumnName("activo");

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne(x => x.Room).WithMany(x => x.Racks)
            .HasForeignKey(x => new { x.CompanyId, x.RoomId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DeviceModelConfiguration : IEntityTypeConfiguration<DeviceModel>
{
    public void Configure(EntityTypeBuilder<DeviceModel> b)
    {
        b.ToTable("modelos_dispositivo");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Brand).HasColumnName("marca").HasMaxLength(100);
        b.Property(x => x.ModelName).HasColumnName("modelo").HasMaxLength(100);
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.HeightUnits).HasColumnName("altura_u");
        b.Property(x => x.PortCount).HasColumnName("cantidad_puertos");
        b.Property(x => x.PortDescription).HasColumnName("tipo_puertos").HasMaxLength(100);
        b.Property(x => x.IsActiveDevice).HasColumnName("es_activo");
    }
}

internal sealed class NetworkDeviceConfiguration : IEntityTypeConfiguration<NetworkDevice>
{
    public void Configure(EntityTypeBuilder<NetworkDevice> b)
    {
        b.ToTable("dispositivos_red");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.RackId).HasColumnName("gabinete_id");
        b.Property(x => x.ModelId).HasColumnName("modelo_id");
        b.Property(x => x.CollectorAgentId).HasColumnName("recolector_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(100);
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.ManagementIp).HasColumnName("ip_gestion").Varchar(45);
        b.Property(x => x.RackPositionU).HasColumnName("posicion_u");
        b.Property(x => x.HeightUnits).HasColumnName("altura_u");
        b.Property(x => x.QrCode).HasColumnName("codigo_qr").DefaultNewGuid();
        b.Property(x => x.SnmpUser).HasColumnName("snmp_usuario").HasMaxLength(100);
        b.Property(x => x.SnmpEncryptedCredential).HasColumnName("snmp_credencial_cifrada");
        b.Property(x => x.Firmware).HasColumnName("firmware").HasMaxLength(100);
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.LastPolledAt).HasColumnName("ultimo_sondeo");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Rack).WithMany(x => x.Devices)
            .HasForeignKey(x => new { x.CompanyId, x.RackId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Agent>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.CollectorAgentId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasMany(x => x.Ports).WithOne(x => x.Device)
            .HasForeignKey(x => new { x.CompanyId, x.DeviceId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PortConfiguration : IEntityTypeConfiguration<Port>
{
    public void Configure(EntityTypeBuilder<Port> b)
    {
        b.ToTable("puertos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.DeviceId).HasColumnName("dispositivo_id");
        b.Property(x => x.Number).HasColumnName("numero");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(50);
        b.Property(x => x.Type).HasColumnName("tipo");
        b.Property(x => x.Vlan).HasColumnName("vlan");
        b.Property(x => x.SpeedMbps).HasColumnName("velocidad_mbps");
        b.Property(x => x.Duplex).HasColumnName("duplex");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.IsTrunk).HasColumnName("es_troncal");
        b.Property(x => x.IsVmHost).HasColumnName("es_host_vm");
        b.Property(x => x.IsManualAssignment).HasColumnName("asignacion_manual");

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
    }
}

internal sealed class MacTableEntryConfiguration : IEntityTypeConfiguration<MacTableEntry>
{
    public void Configure(EntityTypeBuilder<MacTableEntry> b)
    {
        b.ToTable("tabla_mac");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.PortId).HasColumnName("puerto_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.MacAddress).HasColumnName("mac").Varchar(17);
        b.Property(x => x.IpAddress).HasColumnName("ip").Varchar(45);
        b.Property(x => x.Vlan).HasColumnName("vlan");
        b.Property(x => x.Vendor).HasColumnName("fabricante").HasMaxLength(150);
        b.Property(x => x.FirstSeenAt).HasColumnName("primera_vez").DefaultUtcNow();
        b.Property(x => x.LastSeenAt).HasColumnName("ultima_vez").DefaultUtcNow();

        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.PortId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
        // FK simple: ON DELETE SET NULL no puede ser compuesta porque anularía empresa_id.
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class NetworkLinkConfiguration : IEntityTypeConfiguration<NetworkLink>
{
    public void Configure(EntityTypeBuilder<NetworkLink> b)
    {
        b.ToTable("enlaces_red");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.SourcePortId).HasColumnName("puerto_origen_id");
        b.Property(x => x.TargetPortId).HasColumnName("puerto_destino_id");
        b.Property(x => x.Protocol).HasColumnName("protocolo");
        b.Property(x => x.DetectedAt).HasColumnName("detectado").DefaultUtcNow();

        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.SourcePortId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.TargetPortId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PortMetricConfiguration : IEntityTypeConfiguration<PortMetric>
{
    public void Configure(EntityTypeBuilder<PortMetric> b)
    {
        b.ToTable("metricas_puerto");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.PortId).HasColumnName("puerto_id");
        b.Property(x => x.Timestamp).HasColumnName("fecha_hora").DefaultUtcNow();
        b.Property(x => x.BytesIn).HasColumnName("bytes_entrada");
        b.Property(x => x.BytesOut).HasColumnName("bytes_salida");
        b.Property(x => x.ErrorsIn).HasColumnName("errores_entrada");
        b.Property(x => x.ErrorsOut).HasColumnName("errores_salida");
        b.Property(x => x.Discards).HasColumnName("descartes");

        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.PortId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class NetworkOutletConfiguration : IEntityTypeConfiguration<NetworkOutlet>
{
    public void Configure(EntityTypeBuilder<NetworkOutlet> b)
    {
        b.ToTable("puntos_red");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.BranchId).HasColumnName("sucursal_id");
        b.Property(x => x.Code).HasColumnName("codigo").HasMaxLength(30);
        b.Property(x => x.Location).HasColumnName("ubicacion").HasMaxLength(200);
        b.Property(x => x.QrCode).HasColumnName("codigo_qr").DefaultNewGuid();

        b.HasAlternateKey(x => new { x.CompanyId, x.Id });
        b.HasOne<Branch>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.BranchId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CableConfiguration : IEntityTypeConfiguration<Cable>
{
    public void Configure(EntityTypeBuilder<Cable> b)
    {
        b.ToTable("cableado");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.PortAId).HasColumnName("puerto_a_id");
        b.Property(x => x.PortBId).HasColumnName("puerto_b_id");
        b.Property(x => x.OutletId).HasColumnName("punto_red_id");
        b.Property(x => x.CableType).HasColumnName("tipo_cable");
        b.Property(x => x.Color).HasColumnName("color").HasMaxLength(30);
        b.Property(x => x.Label).HasColumnName("etiqueta").HasMaxLength(50);
        b.Property(x => x.Notes).HasColumnName("notas").HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.PortAId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<Port>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.PortBId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
        b.HasOne<NetworkOutlet>().WithMany()
            .HasForeignKey(x => new { x.CompanyId, x.OutletId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id })
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class MachineNetworkLocationConfiguration : IEntityTypeConfiguration<MachineNetworkLocation>
{
    public void Configure(EntityTypeBuilder<MachineNetworkLocation> b)
    {
        b.ToView("vw_ubicacion_red_maquina");
        b.HasNoKey();
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.MachineId).HasColumnName("maquina_id");
        b.Property(x => x.MachineName).HasColumnName("maquina");
        b.Property(x => x.MacAddress).HasColumnName("mac").Varchar(17);
        b.Property(x => x.IpAddress).HasColumnName("ip").Varchar(45);
        b.Property(x => x.Vlan).HasColumnName("vlan");
        b.Property(x => x.BranchName).HasColumnName("sucursal");
        b.Property(x => x.RoomName).HasColumnName("sala");
        b.Property(x => x.RackName).HasColumnName("gabinete");
        b.Property(x => x.DeviceId).HasColumnName("dispositivo_id");
        b.Property(x => x.SwitchName).HasColumnName("switch_nombre");
        b.Property(x => x.PortId).HasColumnName("puerto_id");
        b.Property(x => x.PortNumber).HasColumnName("puerto_numero");
        b.Property(x => x.PortName).HasColumnName("puerto_nombre");
        b.Property(x => x.LastSeenAt).HasColumnName("ultima_vez");
    }
}
