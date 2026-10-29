using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MonitoreoTI.Domain.Entities.Companies;
using MonitoreoTI.Domain.Entities.Security;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(50);
        b.Property(x => x.Description).HasColumnName("descripcion").HasMaxLength(200);

        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permisos");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Key).HasColumnName("clave").HasMaxLength(100);
        b.Property(x => x.Description).HasColumnName("descripcion").HasMaxLength(200);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("rol_permiso");
        b.HasKey(x => new { x.RoleId, x.PermissionId });
        b.Property(x => x.RoleId).HasColumnName("rol_id");
        b.Property(x => x.PermissionId).HasColumnName("permiso_id");

        b.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("usuarios");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("nombre").HasMaxLength(150);
        b.Property(x => x.Email).HasColumnName("correo").HasMaxLength(200);
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
        b.Property(x => x.TotpSecret).HasColumnName("totp_secreto").HasMaxLength(255);
        b.Property(x => x.IsSuperAdmin).HasColumnName("es_superadmin");
        b.Property(x => x.FailedLoginAttempts).HasColumnName("intentos_fallidos");
        b.Property(x => x.LockedUntil).HasColumnName("bloqueado_hasta");
        b.Property(x => x.IsActive).HasColumnName("activo");
        b.Property(x => x.LastLoginAt).HasColumnName("ultimo_acceso");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasMany(x => x.Companies).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class UserCompanyConfiguration : IEntityTypeConfiguration<UserCompany>
{
    public void Configure(EntityTypeBuilder<UserCompany> b)
    {
        b.ToTable("usuario_empresa");
        b.HasKey(x => new { x.UserId, x.CompanyId });
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.RoleId).HasColumnName("rol_id");
        b.Property(x => x.IsActive).HasColumnName("activo");
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();

        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(255);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
        b.Property(x => x.ExpiresAt).HasColumnName("expira");
        b.Property(x => x.RevokedAt).HasColumnName("revocado");
        b.Property(x => x.ReplacedById).HasColumnName("reemplazado_por");
        b.Property(x => x.IpAddress).HasColumnName("ip_origen").Varchar(45);

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedById).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("tokens_restablecimiento");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(255);
        b.Property(x => x.CreatedAt).HasColumnName("creado").DefaultUtcNow();
        b.Property(x => x.ExpiresAt).HasColumnName("expira");
        b.Property(x => x.UsedAt).HasColumnName("usado");
        b.Property(x => x.IpAddress).HasColumnName("ip_origen").Varchar(45);

        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("auditoria");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CompanyId).HasColumnName("empresa_id");
        b.Property(x => x.UserId).HasColumnName("usuario_id");
        b.Property(x => x.Timestamp).HasColumnName("fecha").DefaultUtcNow();
        b.Property(x => x.Action).HasColumnName("accion").Varchar(50);
        b.Property(x => x.Entity).HasColumnName("entidad").Varchar(50);
        b.Property(x => x.EntityId).HasColumnName("entidad_id").HasMaxLength(50);
        b.Property(x => x.Details).HasColumnName("detalle");
        b.Property(x => x.SourceIp).HasColumnName("ip_origen").Varchar(45);

        b.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}
