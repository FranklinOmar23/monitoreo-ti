namespace MonitoreoTI.Api.Auth;

/// <summary>
/// El panel (Electron) llama al API desde su propio origen, así que el navegador exige CORS.
/// Solo se aceptan los orígenes configurados en "Cors:PanelOrigins"; en desarrollo, el servidor de Vite.
/// No se permiten credenciales (cookies): la sesión viaja en la cabecera Authorization.
/// </summary>
public static class CorsExtensions
{
    public const string PanelPolicy = "panel";

    private static readonly string[] DevelopmentOrigins = ["http://localhost:5173", "http://127.0.0.1:5173"];

    public static IServiceCollection AddPanelCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:PanelOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0 && environment.IsDevelopment())
            origins = DevelopmentOrigins;

        services.AddCors(options => options.AddPolicy(PanelPolicy, policy => policy
            .WithOrigins(origins)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            // X-Requested-With y X-SignalR-User-Agent los envía el cliente de SignalR (tiempo real).
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Requested-With", "X-SignalR-User-Agent")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }
}
