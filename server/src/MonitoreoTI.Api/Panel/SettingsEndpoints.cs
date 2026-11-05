using System.Collections.Concurrent;
using System.Security.Claims;
using FluentValidation;
using MonitoreoTI.Api.Auth;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Settings;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Settings;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>Configuración de la empresa (W-40), con el permiso configuracion.editar; y la apariencia para todo el panel.</summary>
public static class SettingsEndpoints
{
    /// <summary>Último correo de prueba de cada usuario: uno por minuto.</summary>
    private static readonly ConcurrentDictionary<int, DateTime> TestEmails = new();

    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // Logo y color de la empresa en la que se trabaja: los pinta el panel de cualquier usuario.
        app.MapGet("/api/v1/apariencia", async (ClaimsPrincipal principal, CompanySettingsService settings, CancellationToken ct) =>
            AuthEndpoints.CompanyId(principal) is int companyId
                ? Results.Ok(await settings.AppearanceAsync(companyId, ct))
                : Results.Ok(new AppearanceData(null, null)))
            .RequireAuthorization();

        var config = app.MapGroup("/api/v1/configuracion").RequireAuthorization(PermissionPolicies.SettingsEdit);

        config.MapGet("/empresa", (CompanySettingsService s, CancellationToken ct) => s.GetCompanyAsync(ct));
        config.MapPut("/empresa", async (CompanyInput input, IValidator<CompanyInput> v, CompanySettingsService s, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await s.UpdateCompanyAsync(input, ct)));

        config.MapPut("/apariencia/color", async (ColorInput input, IValidator<ColorInput> v, CompanySettingsService s, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await s.SetColorAsync(input.Color, ct)));
        config.MapPut("/apariencia/logo", async (LogoInput input, CompanySettingsService s, CancellationToken ct) =>
            ToResult(await s.SetLogoAsync(input.Datos ?? "", ct)));
        config.MapDelete("/apariencia/logo", async (CompanySettingsService s, CancellationToken ct) =>
            ToResult(await s.RemoveLogoAsync(ct)));

        // Departamentos (decisión 79): aquí solo se consultan todos; se crean y cambian en cada sucursal.
        config.MapGet("/departamentos", (MonitoreoTI.Infrastructure.Panel.Departments d, CancellationToken ct) => d.ListAsync(null, ct));

        config.MapGet("/seguridad", (CompanySettingsService s, CancellationToken ct) => s.SecurityAsync(ct));
        config.MapPut("/seguridad", (SecuritySettings input, CompanySettingsService s, CancellationToken ct) => s.SetSecurityAsync(input, ct));

        // Estado del correo: el modo y el remitente, nunca usuario ni clave.
        config.MapGet("/correo", (IEmailSender mailer, IServiceProvider services) =>
        {
            var smtp = services.GetService<SmtpOptions>();
            return mailer switch
            {
                SmtpEmailSender when smtp is not null => new EmailStatus("smtp", smtp.From, smtp.Host),
                DevEmailSender => new EmailStatus("archivo", null, null),
                _ => new EmailStatus("sin_configurar", null, null),
            };
        });
        config.MapPost("/correo/prueba", async (MonitoreoDbContext db, IEmailSender mailer, ICurrentUser user, IAuditTrail audit,
            TimeProvider clock, ILoggerFactory logs, CancellationToken ct) =>
        {
            var userId = user.UserId;
            var now = clock.GetUtcNow().UtcDateTime;
            if (TestEmails.TryGetValue(userId, out var last) && now - last < TimeSpan.FromMinutes(1))
                return Results.Problem("Ya se mandó un correo de prueba hace menos de un minuto.", statusCode: StatusCodes.Status429TooManyRequests);
            TestEmails[userId] = now;

            var to = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(to))
                return Results.Problem("La sesión no tiene correo.", statusCode: StatusCodes.Status400BadRequest);
            try
            {
                await mailer.SendAsync(to, "Correo de prueba de MonitoreoTI",
                    "Si lee esto, el servidor de MonitoreoTI puede enviar correos (alertas críticas y enlaces de contraseña).", ct);
            }
            catch (Exception ex) when (ex is System.Net.Mail.SmtpException or IOException or InvalidOperationException)
            {
                // Solo el tipo del error: el mensaje del servidor de correo puede traer datos de la cuenta.
                logs.CreateLogger("Configuracion").LogWarning("Falló el correo de prueba: {Tipo}", ex.GetType().Name);
                return Results.Problem("El servidor de correo no aceptó el envío. Revise la configuración de Correo:Smtp en el servidor.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            await audit.RecordAsync("configuracion.correo_prueba", "usuarios", userId.ToString(), null, null, ct);
            return Results.Ok(new { enviado = mailer.Delivers, correo = to });
        });
    }
}
