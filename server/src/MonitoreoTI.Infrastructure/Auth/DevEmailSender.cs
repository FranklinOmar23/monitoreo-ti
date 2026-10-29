using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Auth;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>
/// Solo desarrollo: guarda cada correo como archivo de texto en vez de enviarlo. No se escribe en el log
/// porque el cuerpo lleva tokens (sección 15). En producción se usa un remitente SMTP real.
/// </summary>
public sealed class DevEmailSender(string folder, ILogger<DevEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, $"Para: {to}\nAsunto: {subject}\n\n{body}\n", cancellationToken);
        logger.LogInformation("Correo de desarrollo guardado en {File}.", file);
    }
}

/// <summary>Sin SMTP configurado fuera de desarrollo: el correo no se envía y queda el error en el log (sin el cuerpo).</summary>
public sealed class UnconfiguredEmailSender(ILogger<UnconfiguredEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogError("No hay servidor de correo configurado; no se envió \"{Subject}\".", subject);
        return Task.CompletedTask;
    }
}
