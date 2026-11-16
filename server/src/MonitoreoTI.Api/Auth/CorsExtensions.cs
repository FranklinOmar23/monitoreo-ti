namespace MonitoreoTI.Api.Auth;

/// <summary>
/// El panel (Electron) llama al API desde su propio origen, así que el navegador exige CORS.
/// Solo se aceptan los orígenes configurados en "Cors:PanelOrigins"; en desarrollo, el servidor de Vite.
/// No se permiten credenciales (cookies): la sesión viaja en la cabecera Authorization.
/// </summary>
public static class CorsExtensions
{
    public const string PanelPolicy = "panel";

    public static IServiceCollection AddPanelCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:PanelOrigins").Get<string[]>() ?? [];
        // En desarrollo, el servidor de Vite en cualquier puerto local: si el 5173 está ocupado por otro proyecto,
        // Vite toma el siguiente (5174…) y el panel no debe quedarse sin poder llamar al API.
        var anyLocalPort = origins.Length == 0 && environment.IsDevelopment();

        services.AddCors(options => options.AddPolicy(PanelPolicy, policy => (anyLocalPort
                ? policy.SetIsOriginAllowed(IsLocalDevOrigin)
                : policy.WithOrigins(origins))
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            // X-Requested-With y X-SignalR-User-Agent los envía el cliente de SignalR (tiempo real).
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Requested-With", "X-SignalR-User-Agent")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }

    /// <summary>http://localhost:&lt;puerto&gt; o http://127.0.0.1:&lt;puerto&gt; (solo desarrollo).</summary>
    public static bool IsLocalDevOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host == "localhost" || uri.Host == "127.0.0.1");
}
