using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MonitoreoTI.Api.Health;

/// <summary>Respuesta JSON de /health. No incluye detalles de excepciones.</summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new
        {
            status = report.Status.ToString(),
            duration = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOptions));
    }
}
