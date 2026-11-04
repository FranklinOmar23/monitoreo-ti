using System.Net;
using System.Net.Mail;
using MonitoreoTI.Application.Auth;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>Servidor de correo (sección "Correo:Smtp" de la configuración; la clave va en user-secrets o variables de entorno).</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>STARTTLS o TLS (recomendado; Gmail y Office 365 lo exigen).</summary>
    public bool UseTls { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Remitente, por ejemplo "MonitoreoTI &lt;alertas@empresa.do&gt;".</summary>
    public string From { get; set; } = "";
}

/// <summary>Envía por SMTP con System.Net.Mail (decisión 48: sin librerías fuera de §4.3). Nunca escribe el cuerpo en el log.</summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrEmpty(options.User) ? null : new NetworkCredential(options.User, options.Password),
        };
        using var message = new MailMessage(new MailAddress(options.From), new MailAddress(to))
        {
            Subject = subject,
            Body = body,
            IsBodyHtml = false,
        };
        await client.SendMailAsync(message, cancellationToken);
    }
}
