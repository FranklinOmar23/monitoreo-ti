using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Companies;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("empresas");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(150);
        b.Property(x => x.TaxId).HasColumnName("rnc").Varchar(15);
        b.Property(x => x.ContactEmail).HasColumnName("correo_contacto").HasMaxLength(200);
        b.Property(x => x.Phone).HasColumnName("telefono").Varchar(20);
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.Status).HasColumnName("estado");
        b.Property(x => x.AutoUpdateAgents).HasColumnName("actualizacion_automatica");
        b.Property(x => x.AskRemoteConsent).HasColumnName("pedir_permiso_remoto");
        b.Property(x => x.Logo).HasColumnName("logo");
        b.Property(x => x.LogoType).HasColumnName("logo_tipo").Varchar(20);
        b.Property(x => x.BrandColor).HasColumnName("color_primario").HasColumnType("char(7)").IsUnicode(false);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("planes");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(50);
        b.Property(x => x.MaxAgents).HasColumnName("max_agentes");
        b.Property(x => x.MaxBranches).HasColumnName("max_sucursales");
        b.Property(x => x.MaxUsers).HasColumnName("max_usuarios");
        b.Property(x => x.MonthlyPrice).HasColumnName("precio_mensual").HasPrecision(10, 2);
        b.Property(x => x.IsActive).HasColumnName("activo");

        b.HasMany(x => x.Modules).WithOne().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlanModuleConfiguration : IEntityTypeConfiguration<PlanModule>
{
    public void Configure(EntityTypeBuilder<PlanModule> b)
    {
        b.ToTable("plan_modulos");
        b.HasKey(x => new { x.PlanId, x.Module });
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.Module).HasColumnName("modulo");
    }
}

internal sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("suscripciones");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.StartDate).HasColumnName("inicio");
        b.Property(x => x.EndDate).HasColumnName("fin");
        b.Property(x => x.Status).HasColumnName("estado");
        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CompanyKeyConfiguration : IEntityTypeConfiguration<CompanyKey>
{
    public void Configure(EntityTypeBuilder<CompanyKey> b)
    {
        b.ToTable("empresa_llaves");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.PublicKey).HasColumnName("llave_publica").HasMaxLength(500);
        b.Property(x => x.EncryptedPrivateKey).HasColumnName("llave_privada_cifrada");
        b.Property(x => x.CreatedAt).HasColumnName("creada").DefaultUtcNow();
        b.Property(x => x.IsActive).HasColumnName("activa");
    }
}

internal sealed class SupportAccessConfiguration : IEntityTypeConfiguration<SupportAccess>
{
    public void Configure(EntityTypeBuilder<SupportAccess> b)
    {
        b.ToTable("accesos_soporte");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.SuperAdminId).HasColumnName("superadmin_id");
        b.Property(x => x.AuthorizedById).HasColumnName("autorizado_por");
        b.Property(x => x.StartsAt).HasColumnName("desde");
        b.Property(x => x.EndsAt).HasColumnName("hasta");
        b.Property(x => x.Reason).HasColumnName("motivo").HasMaxLength(500);
        b.Property(x => x.RevokedAt).HasColumnName("revocado");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
    }
}
