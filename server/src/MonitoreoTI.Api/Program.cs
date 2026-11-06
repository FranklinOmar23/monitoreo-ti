using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Api.Auth;
using MonitoreoTI.Api.Health;
using MonitoreoTI.Api.Panel;
using MonitoreoTI.Infrastructure;
using MonitoreoTI.Infrastructure.HealthChecks;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, logger) => logger.ReadFrom.Configuration(context.Configuration));

var connectionString = builder.Configuration.GetConnectionString("MonitoreoTI");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Falta la cadena de conexión 'ConnectionStrings:MonitoreoTI'. " +
        "Configúrela con 'dotnet user-secrets' o la variable de entorno ConnectionStrings__MonitoreoTI.");
}

builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddAgentGateway(builder.Configuration);
builder.Services.AddPanelAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddPanelCors(builder.Configuration, builder.Environment);
builder.Services.AddPanelApi();
// Swagger solo en desarrollo (/swagger).
if (builder.Environment.IsDevelopment())
    builder.Services.AddDevSwagger();

var app = builder.Build();

app.UseSerilogRequestLogging();
if (app.Environment.IsDevelopment())
    app.UseDevSwagger();
app.UseCors(CorsExtensions.PanelPolicy);
app.UseRateLimiter();
app.UseAuthentication();
app.UseTenantFromToken();
app.UseAuthorization();

app.UseAgentGateway();
app.MapAuthEndpoints();
app.MapPanelApi();
app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponseWriter.WriteAsync });

app.Run();

// Expuesto para las pruebas de integración (WebApplicationFactory).
public partial class Program;
