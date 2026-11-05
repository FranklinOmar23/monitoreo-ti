using FluentValidation;
using MonitoreoTI.Api.Remote;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Application.Users;
using MonitoreoTI.Infrastructure.Network;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Remote;
using MonitoreoTI.Infrastructure.Users;

namespace MonitoreoTI.Api.Panel;

public static class PanelExtensions
{
    public static IServiceCollection AddPanelApi(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<CurrentUser>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<PanelQueries>();
        services.AddScoped<PanelCommands>();
        services.AddScoped<AlertQueries>();
        services.AddScoped<AlertCommands>();
        services.AddSingleton<IValidator<AlertActionInput>, AlertActionInputValidator>();
        services.AddSingleton<IValidator<RuleInput>, RuleInputValidator>();
        services.AddScoped<TicketQueries>();
        services.AddScoped<TicketCommands>();
        services.AddSingleton<IValidator<TicketInput>, TicketInputValidator>();
        services.AddSingleton<IValidator<TicketUpdateInput>, TicketUpdateInputValidator>();
        services.AddSingleton<IValidator<TicketStatusInput>, TicketStatusInputValidator>();
        services.AddSingleton<IValidator<TicketCommentInput>, TicketCommentInputValidator>();
        services.AddSingleton<IValidator<TicketAttachmentInput>, TicketAttachmentInputValidator>();
        services.AddSingleton(sp =>
        {
            // Una ruta relativa se toma desde la carpeta del API, no desde donde se lanzó el proceso.
            var folder = sp.GetRequiredService<IConfiguration>()["Adjuntos:Carpeta"] ?? "App_Data/adjuntos";
            return new AttachmentOptions { Folder = Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, folder) };
        });
        services.AddSingleton<IAttachmentStore, FileAttachmentStore>();
        services.AddScoped<NetworkQueries>();
        services.AddScoped<NetworkCommands>();
        services.AddScoped<NetworkImport>();
        services.AddScoped<NetworkInsights>();
        services.AddScoped<IpAddresses>();
        services.AddScoped<RemoteInstalls>();
        services.AddSingleton<IValidator<SubnetInput>, SubnetInputValidator>();
        services.AddSingleton<IValidator<IpReservationInput>, IpReservationInputValidator>();
        services.AddScoped<Maintenances>();
        services.AddScoped<RemoteCommands>();
        services.AddScoped<RemoteAccessGuard>();
        services.AddScoped<RemoteSessions>();
        services.AddScoped<ScheduledTasks>();
        services.AddSingleton<IValidator<ScheduledTaskInput>, ScheduledTaskInputValidator>();
        services.AddSingleton<IValidator<SessionInput>, SessionInputValidator>();
        services.AddSingleton<IValidator<ScriptInput>, ScriptInputValidator>();
        services.AddSingleton<IValidator<RunInput>, RunInputValidator>();
        services.AddSingleton<IValidator<MaintenanceInput>, MaintenanceInputValidator>();
        services.AddSingleton<IValidator<MaintenanceDoneInput>, MaintenanceDoneInputValidator>();
        services.AddSingleton<IValidator<ImportInput>, ImportInputValidator>();
        services.AddSingleton<IValidator<RoomInput>, RoomInputValidator>();
        services.AddSingleton<IValidator<RackInput>, RackInputValidator>();
        services.AddSingleton<IValidator<DeviceModelInput>, DeviceModelInputValidator>();
        services.AddSingleton<IValidator<DeviceInput>, DeviceInputValidator>();
        services.AddSingleton<IValidator<ApiTestInput>, ApiTestInputValidator>();
        services.AddSingleton<IValidator<RemoteInstallInput>, RemoteInstallInputValidator>();
        services.AddSingleton<IValidator<PortInput>, PortInputValidator>();
        services.AddSingleton<IValidator<OutletInput>, OutletInputValidator>();
        services.AddSingleton<IValidator<CableInput>, CableInputValidator>();
        services.AddSingleton<IValidator<MachineInput>, MachineInputValidator>();
        services.AddSingleton<IValidator<BranchInput>, BranchInputValidator>();
        services.AddSingleton<IValidator<EnrollmentTokenInput>, EnrollmentTokenInputValidator>();
        services.AddScoped<UserAdministration>();
        services.AddScoped<RoleAdministration>();
        services.AddScoped<UserProfile>();
        services.AddScoped<MonitoreoTI.Infrastructure.Settings.CompanySettingsService>();
        services.AddScoped<MonitoreoTI.Infrastructure.Reports.ReportQueries>();
        services.AddSingleton<IValidator<MonitoreoTI.Application.Settings.CompanyInput>, MonitoreoTI.Application.Settings.CompanyInputValidator>();
        services.AddSingleton<IValidator<MonitoreoTI.Application.Settings.ColorInput>, MonitoreoTI.Application.Settings.ColorInputValidator>();
        services.AddSingleton<IValidator<MonitoreoTI.Application.Settings.DepartmentInput>, MonitoreoTI.Application.Settings.DepartmentInputValidator>();
        services.AddSingleton<IValidator<UserInput>, UserInputValidator>();
        services.AddSingleton<IValidator<RoleInput>, RoleInputValidator>();
        services.AddSingleton<IValidator<ProfileInput>, ProfileInputValidator>();
        services.AddSingleton<IValidator<ChangePasswordInput>, ChangePasswordInputValidator>();
        services.AddSignalR();
        services.AddSingleton<IRealtimeNotifier, SignalRNotifier>();
        return services;
    }

    public static WebApplication MapPanelApi(this WebApplication app)
    {
        app.MapPanelEndpoints();
        app.MapPanelWriteEndpoints();
        app.MapAlertEndpoints();
        app.MapTicketEndpoints();
        app.MapNetworkEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapRemoteEndpoints();
        app.MapUserEndpoints();
        app.MapReportEndpoints();
        app.MapPanelUpdateEndpoints();
        app.MapSettingsEndpoints();
        app.MapHub<RealtimeHub>(RealtimeHub.Path);
        return app;
    }
}
