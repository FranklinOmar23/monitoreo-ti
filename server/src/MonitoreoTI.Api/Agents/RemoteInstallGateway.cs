using System.Net.WebSockets;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Api.Agents;

/// <summary>Manda "instalar_agente" al recolector de la subred (decisión 77).</summary>
public sealed class RemoteInstallDispatcher(AgentConnectionRegistry registry, ILogger<RemoteInstallDispatcher> logger) : IRemoteInstallDispatcher
{
    public async Task<bool> SendAsync(int companyId, int agentId, RemoteInstallRequest request, CancellationToken cancellationToken)
    {
        if (registry.Get(agentId) is not { } connection || connection.Identity.CompanyId != companyId)
            return false;
        try
        {
            await connection.SendAsync("instalar_agente", new
            {
                instalacion_id = request.InstalacionId,
                ip = request.Ip,
                cuenta = request.Cuenta,
                servidor = request.Servidor,
                token = request.Token,
                instalador = new
                {
                    version = request.Instalador.Version,
                    url = request.Instalador.Url,
                    sha256 = request.Instalador.Sha256,
                    tamano = request.Instalador.Tamano,
                    firma = request.Instalador.Firma,
                },
            }, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            logger.LogInformation("Agente {AgentId}: no se pudo pedir la instalación {InstallId}.", agentId, request.InstalacionId);
            return false;
        }
    }
}

public sealed class RemoteInstallStatusHandler(IRemoteInstallStatus store, TimeProvider clock, ILogger<RemoteInstallStatusHandler> logger)
    : TelemetryHandler<InstalacionEstadoData>(logger)
{
    public override string Tipo => "instalacion_estado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, InstalacionEstadoData data, CancellationToken cancellationToken) =>
        store.SaveAsync(identity, data, clock.GetUtcNow().UtcDateTime, cancellationToken);
}
