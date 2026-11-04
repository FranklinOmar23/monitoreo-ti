using System.Text.RegularExpressions;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Actualización automática del panel (decisión 70): electron-updater pide latest.yml y el instalador a esta ruta, sin
/// sesión (el panel se actualiza aunque nadie haya entrado). Se sirven solo esos archivos de la carpeta configurada en
/// PanelActualizaciones:Carpeta, que llena quien publica una versión.
/// </summary>
public static partial class PanelUpdateEndpoints
{
    [GeneratedRegex(@"^(latest\.yml|MonitoreoTI-Panel-Setup-\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?\.exe(\.blockmap)?)$")]
    private static partial Regex AllowedFile();

    public static void MapPanelUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/panel/actualizacion/{archivo}", (string archivo, IConfiguration configuration) =>
        {
            var folder = configuration["PanelActualizaciones:Carpeta"];
            if (string.IsNullOrWhiteSpace(folder) || !AllowedFile().IsMatch(archivo))
                return Results.NotFound();

            var path = Path.Combine(Path.GetFullPath(folder), archivo);
            if (!File.Exists(path))
                return Results.NotFound();

            var contentType = archivo.EndsWith(".yml", StringComparison.Ordinal) ? "text/yaml"
                : archivo.EndsWith(".exe", StringComparison.Ordinal) ? "application/vnd.microsoft.portable-executable"
                : "application/octet-stream";
            // Rangos: electron-updater baja solo los bloques que cambiaron (blockmap).
            return Results.File(path, contentType, enableRangeProcessing: true);
        });
    }
}
