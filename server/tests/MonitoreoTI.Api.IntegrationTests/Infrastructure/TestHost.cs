using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;

namespace MonitoreoTI.Api.IntegrationTests.Infrastructure;

/// <summary>Configuración común del API en memoria para las pruebas.</summary>
public static class TestHost
{
    // Llaves aleatorias por corrida: las pruebas nunca dependen de secretos guardados en el repositorio.
    public static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    public static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Carpeta donde el remitente de desarrollo deja los correos (recuperar contraseña).</summary>
    public static readonly string EmailFolder = Path.Combine(Path.GetTempPath(), "monitoreoti-pruebas-correos", Guid.NewGuid().ToString("N"));

    /// <summary>Carpeta de adjuntos de tickets (ruta absoluta: no se mezcla con la del API de desarrollo).</summary>
    public static readonly string AttachmentFolder = Path.Combine(Path.GetTempPath(), "monitoreoti-pruebas-adjuntos", Guid.NewGuid().ToString("N"));

    // UseSetting (no ConfigureAppConfiguration): Program lee la configuración antes de Build().
    public static void Configure(IWebHostBuilder builder, string connectionString)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:MonitoreoTI", connectionString);
        builder.UseSetting("Auth:SigningKey", SigningKey);
        builder.UseSetting("Auth:EncryptionKey", EncryptionKey);
        builder.UseSetting("Auth:RateLimitPerMinute", "10000");
        builder.UseSetting("Email:DevFolder", EmailFolder);
        builder.UseSetting("Agents:RegisterRateLimitPerMinute", "10000");
        // El servicio de presencia se prueba llamando a TickAsync, no esperando el temporizador.
        builder.UseSetting("Agents:Presence:Interval", "01:00:00");
        // Cada API de prueba hace un ciclo al arrancar: con 3 minutos, una corrida larga (base lenta) marcaba
        // desconectados los equipos de ejemplo y fallaban las pruebas que leen sus estados. Las pruebas de presencia
        // envejecen el contacto más que esto.
        builder.UseSetting("Agents:Presence:DisconnectAfter", "06:00:00");
        // Las pruebas vacían el escritor de métricas a mano con FlushAsync.
        builder.UseSetting("Agents:Metrics:FlushInterval", "01:00:00");
        // El motor de alertas se prueba llamando a TickAsync.
        builder.UseSetting("Alerts:Interval", "01:00:00");
        builder.UseSetting("Adjuntos:Carpeta", AttachmentFolder);
    }
}
