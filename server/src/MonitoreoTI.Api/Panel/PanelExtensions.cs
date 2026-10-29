using FluentValidation;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Network;
using MonitoreoTI.Infrastructure.Panel;

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
        services.AddSingleton<IValidator<RoomInput>, RoomInputValidator>();
        services.AddSingleton<IValidator<RackInput>, RackInputValidator>();
        services.AddSingleton<IValidator<DeviceModelInput>, DeviceModelInputValidator>();
        services.AddSingleton<IValidator<DeviceInput>, DeviceInputValidator>();
        services.AddSingleton<IValidator<PortInput>, PortInputValidator>();
        services.AddSingleton<IValidator<OutletInput>, OutletInputValidator>();
        services.AddSingleton<IValidator<CableInput>, CableInputValidator>();
        services.AddSingleton<IValidator<MachineInput>, MachineInputValidator>();
        services.AddSingleton<IValidator<BranchInput>, BranchInputValidator>();
        services.AddSingleton<IValidator<EnrollmentTokenInput>, EnrollmentTokenInputValidator>();
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
        app.MapHub<RealtimeHub>(RealtimeHub.Path);
        return app;
    }
}
