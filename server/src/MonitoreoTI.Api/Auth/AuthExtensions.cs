using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Infrastructure.Auth;

namespace MonitoreoTI.Api.Auth;

public static class AuthExtensions
{
    /// <summary>Prefijo de las políticas por permiso: RequireAuthorization("perm:equipos.ver").</summary>
    public const string PermissionPolicyPrefix = "perm:";

    public static string Permission(string key) => PermissionPolicyPrefix + key;

    public static IServiceCollection AddPanelAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<AuthTokenService>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<TwoFactor>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddSingleton<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddSingleton<IValidator<ChallengeCodeRequest>, ChallengeCodeRequestValidator>();
        services.AddSingleton<IValidator<PasswordResetConfirmRequest>, PasswordResetConfirmRequestValidator>();

        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            var folder = configuration["Email:DevFolder"] ?? Path.Combine(Path.GetTempPath(), "monitoreoti-correos");
            services.AddSingleton<IEmailSender>(sp => new DevEmailSender(folder, sp.GetRequiredService<ILogger<DevEmailSender>>()));
        }
        else
        {
            services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<AuthTokenService>((jwt, tokens) =>
            {
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = tokens.AccessValidationParameters();
                // SignalR (etapa 2.3) manda el token por query string en la conexión WebSocket.
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            context.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        var perMinute = configuration.GetValue("Auth:RateLimitPerMinute", 20);
        services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(limiter =>
            limiter.AddPolicy(AuthEndpoints.RateLimitPolicy, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) })));

        return services;
    }

    /// <summary>
    /// Después de autenticar: fija la empresa del token en el contexto de la petición. De ahí la toman el filtro
    /// de EF Core y el contexto de sesión de SQL Server (Row-Level Security). Nunca sale del cuerpo ni de la URL.
    /// </summary>
    public static IApplicationBuilder UseTenantFromToken(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
            {
                var tenant = http.RequestServices.GetRequiredService<TenantContext>();
                if (http.User.HasClaim(AuthClaims.SuperAdmin, "1"))
                    tenant.SetSuperAdmin();
                else if (AuthEndpoints.CompanyId(http.User) is int companyId)
                    tenant.SetCompany(companyId);
            }
            await next();
        });
}

/// <summary>
/// Crea al vuelo una política por permiso: "perm:equipos.ver" exige el claim perm=equipos.ver;
/// "perm:tickets.crear|tickets.gestionar" exige cualquiera de los dos.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(AuthExtensions.PermissionPolicyPrefix, StringComparison.Ordinal))
            return await base.GetPolicyAsync(policyName);

        var key = policyName[AuthExtensions.PermissionPolicyPrefix.Length..];
        return new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthClaims.Permission, key.Split('|'))
            .Build();
    }
}
