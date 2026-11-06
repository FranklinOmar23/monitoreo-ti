using Microsoft.OpenApi.Models;

namespace MonitoreoTI.Api.Health;

/// <summary>
/// Swagger (solo en desarrollo): /swagger lista todos los endpoints del API agrupados por recurso
/// (/api/v1/maquinas → "maquinas") y permite probarlos con el token del login (botón Authorize).
/// No se publica en producción: la documentación para terceros es la del artefacto "API de Centinela".
/// </summary>
public static class SwaggerExtensions
{
    public static IServiceCollection AddDevSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Centinela API",
                Version = "v1",
                Description = "API del panel de MonitoreoTI. Inicie sesión con POST /api/v1/auth/login, copie el accessToken " +
                              "y péguelo en Authorize. Los listados responden { data: { data, isThereNextPage, page, limit, total, totalPages } }.",
            });
            // Tipos con el mismo nombre en distintos espacios de nombres no chocan.
            options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);
            options.TagActionsBy(api => [Tag(api.RelativePath)]);
            options.OrderActionsBy(api => $"{Tag(api.RelativePath)} {api.RelativePath} {api.HttpMethod}");

            var bearer = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "El accessToken del login (sin la palabra Bearer).",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            };
            options.AddSecurityDefinition("Bearer", bearer);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement { [bearer] = [] });
        });
        return services;
    }

    public static WebApplication UseDevSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Centinela API v1");
            options.DocumentTitle = "Centinela API";
            options.DisplayRequestDuration();
            options.EnableFilter();
        });
        return app;
    }

    /// <summary>Grupo de un endpoint: el recurso después de /api/v1 (o la primera parte de la ruta).</summary>
    public static string Tag(string? relativePath)
    {
        var parts = (relativePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var start = parts.Length > 2 && parts[0] == "api" && parts[1].StartsWith('v') ? 2 : 0;
        return parts.Length > start ? parts[start] : "general";
    }
}
