using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Entities.Companies;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Infrastructure.Persistence;

/// <summary>
/// Mapeo database-first: el esquema lo definen los scripts de database/, nunca EF Core.
/// No se usan migraciones.
/// </summary>
public class MonitoreoDbContext(DbContextOptions<MonitoreoDbContext> options, ITenantContext tenant)
    : DbContext(options)
{
    // Se leen en cada consulta (EF Core parametriza los miembros del contexto en los filtros).
    private int? CurrentCompanyId => tenant.CompanyId;
    private bool IsSuperAdmin => tenant.IsSuperAdmin;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanModule> PlanModules => Set<PlanModule>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<CompanyKey> CompanyKeys => Set<CompanyKey>();
    public DbSet<SupportAccess> SupportAccesses => Set<SupportAccess>();

    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserCompany> UserCompanies => Set<UserCompany>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<UserBranch> UserBranches => Set<UserBranch>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Component> Components => Set<Component>();
    public DbSet<InstalledSoftware> InstalledSoftware => Set<InstalledSoftware>();
    public DbSet<NetworkInterface> NetworkInterfaces => Set<NetworkInterface>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<WindowsService> Services => Set<WindowsService>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();

    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Rack> Racks => Set<Rack>();
    public DbSet<DeviceModel> DeviceModels => Set<DeviceModel>();
    public DbSet<NetworkDevice> NetworkDevices => Set<NetworkDevice>();
    public DbSet<Port> Ports => Set<Port>();
    public DbSet<MacTableEntry> MacTable => Set<MacTableEntry>();
    public DbSet<NetworkLink> NetworkLinks => Set<NetworkLink>();
    public DbSet<PortMetric> PortMetrics => Set<PortMetric>();
    public DbSet<NetworkOutlet> NetworkOutlets => Set<NetworkOutlet>();
    public DbSet<Cable> Cables => Set<Cable>();
    public DbSet<MachineNetworkLocation> MachineNetworkLocations => Set<MachineNetworkLocation>();

    public DbSet<Metric> Metrics => Set<Metric>();
    public DbSet<HourlyMetric> HourlyMetrics => Set<HourlyMetric>();
    public DbSet<DailyFleetStatus> DailyFleetStatus => Set<DailyFleetStatus>();
    public DbSet<ErrorEvent> ErrorEvents => Set<ErrorEvent>();
    public DbSet<ProcessSnapshot> ProcessSnapshots => Set<ProcessSnapshot>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();

    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<Maintenance> Maintenances => Set<Maintenance>();
    public DbSet<Script> Scripts => Set<Script>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MonitoreoDbContext).Assembly);

        ApplyColumnConventions(modelBuilder);
        ApplyTenantFilters(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Enums como texto VARCHAR con el valor de [EnumMember]; fechas como DATETIME2(0).
    /// </summary>
    private static void ApplyColumnConventions(ModelBuilder modelBuilder)
    {
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

            if (clrType.IsEnum)
            {
                var converterType = typeof(EnumMemberConverter<>).MakeGenericType(clrType);
                property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                property.SetIsUnicode(false);
            }
            else if (clrType == typeof(DateTime))
            {
                property.SetColumnType("datetime2(0)");
            }
        }
    }

    /// <summary>
    /// Filtro global por empresa (segunda capa; la primera es Row-Level Security en SQL Server).
    /// UserCompany y RefreshToken quedan sin filtro: se leen en el login, antes de conocer la empresa.
    /// </summary>
    private void ApplyTenantFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            LambdaExpression? filter = null;

            if (typeof(ITenantEntity).IsAssignableFrom(clrType))
                filter = BuildFilter(clrType, allowGlobal: false);
            else if (typeof(IOptionalTenantEntity).IsAssignableFrom(clrType))
                filter = BuildFilter(clrType, allowGlobal: clrType != typeof(AuditLog));
            else if (clrType == typeof(Company))
                filter = (Expression<Func<Company, bool>>)(c => IsSuperAdmin || c.Id == CurrentCompanyId);
            else if (clrType == typeof(MachineNetworkLocation))
                filter = (Expression<Func<MachineNetworkLocation, bool>>)(l => IsSuperAdmin || l.CompanyId == CurrentCompanyId);

            if (filter is not null)
                entityType.SetQueryFilter(filter);
        }
    }

    // e => IsSuperAdmin || e.CompanyId == CurrentCompanyId [|| e.CompanyId == null]
    private LambdaExpression BuildFilter(Type clrType, bool allowGlobal)
    {
        var entity = Expression.Parameter(clrType, "e");
        var context = Expression.Constant(this);
        var companyId = Expression.Property(entity, nameof(ITenantEntity.CompanyId));
        var current = Expression.Property(context, nameof(CurrentCompanyId));

        Expression sameCompany = companyId.Type == typeof(int)
            ? Expression.Equal(Expression.Convert(companyId, typeof(int?)), current)
            : Expression.Equal(companyId, current);

        Expression body = Expression.OrElse(Expression.Property(context, nameof(IsSuperAdmin)), sameCompany);

        if (allowGlobal)
            body = Expression.OrElse(body, Expression.Equal(companyId, Expression.Constant(null, typeof(int?))));

        return Expression.Lambda(body, entity);
    }

    /// <summary>
    /// La empresa de un registro la asigna el servidor, nunca el cliente, y no puede cambiar.
    /// </summary>
    private void ApplyTenantRules()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case ITenantEntity tenantEntity:
                    ApplyRequiredTenant(entry, tenantEntity);
                    break;
                case IOptionalTenantEntity optionalEntity:
                    ApplyOptionalTenant(entry, optionalEntity);
                    break;
            }
        }
    }

    private void ApplyRequiredTenant(EntityEntry entry, ITenantEntity entity)
    {
        if (entry.State == EntityState.Added)
        {
            if (IsSuperAdmin)
            {
                if (entity.CompanyId <= 0)
                    throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: el superadministrador debe indicar la empresa.");
                return;
            }

            var companyId = CurrentCompanyId
                ?? throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: no hay empresa en el contexto.");

            if (entity.CompanyId != 0 && entity.CompanyId != companyId)
                throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: intento de escribir en otra empresa.");

            entity.CompanyId = companyId;
        }
        else if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantEntity.CompanyId)).IsModified)
        {
            throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: la empresa de un registro no se puede cambiar.");
        }
    }

    private void ApplyOptionalTenant(EntityEntry entry, IOptionalTenantEntity entity)
    {
        if (entry.State == EntityState.Added)
        {
            if (IsSuperAdmin)
                return;

            // Sin empresa en el contexto solo se permiten registros de auditoría del sistema.
            if (CurrentCompanyId is not int companyId)
            {
                if (entity is AuditLog && entity.CompanyId is null)
                    return;
                throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: no hay empresa en el contexto.");
            }

            if (entity.CompanyId is not null && entity.CompanyId != companyId)
                throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: intento de escribir en otra empresa.");

            entity.CompanyId = companyId;
        }
        else if (entry.State == EntityState.Modified && entry.Property(nameof(IOptionalTenantEntity.CompanyId)).IsModified)
        {
            throw new TenantViolationException($"{entry.Metadata.ClrType.Name}: la empresa de un registro no se puede cambiar.");
        }
    }
}
