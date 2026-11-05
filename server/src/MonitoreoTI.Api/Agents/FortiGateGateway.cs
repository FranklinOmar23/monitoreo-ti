using System.Collections.Concurrent;
using System.Net.WebSockets;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Api.Agents;

/// <summary>
/// "Probar API" (decisión 76): manda "probar_api" al recolector y espera su "api_prueba_resultado" con el mismo id de
/// solicitud. Si el agente no contesta en <see cref="Timeout"/>, el panel recibe un aviso en vez de quedarse esperando.
/// </summary>
public sealed class FortiGateApiTester(AgentConnectionRegistry registry, ILogger<FortiGateApiTester> logger) : IFortiGateApiTester
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    private readonly ConcurrentDictionary<string, (int AgentId, TaskCompletionSource<ApiTestResult> Reply)> _pending = new();

    public async Task<ApiTestResult?> TestAsync(
        int companyId, int agentId, string url, string sealedToken, bool acceptSelfSigned, CancellationToken cancellationToken)
    {
        if (registry.Get(agentId) is not { } connection || connection.Identity.CompanyId != companyId)
            return null;
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<ApiTestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (agentId, reply);
        try
        {
            await connection.SendAsync("probar_api",
                new { solicitud_id = id, url, token = sealedToken, aceptar_certificado = acceptSelfSigned }, cancellationToken);
            return await reply.Task.WaitAsync(Timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or TimeoutException)
        {
            logger.LogInformation("Agente {AgentId}: la prueba de API no volvió ({Error}).", agentId, ex.GetType().Name);
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>La respuesta del agente; solo cuenta si viene del mismo agente al que se le pidió.</summary>
    public void Complete(int agentId, ApiPruebaResultadoData data)
    {
        if (!_pending.TryGetValue(data.SolicitudId, out var pending) || pending.AgentId != agentId)
            return;
        pending.Reply.TrySetResult(new ApiTestResult(
            data.Ok, Cut(data.Error, 300), Cut(data.Modelo, 100), Cut(data.Version, 50), Cut(data.Nombre, 100), Cut(data.Serie, 50), data.Ms));
    }

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

public sealed class ApiTestResultHandler(FortiGateApiTester tester, ILogger<ApiTestResultHandler> logger)
    : TelemetryHandler<ApiPruebaResultadoData>(logger)
{
    public override string Tipo => "api_prueba_resultado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, ApiPruebaResultadoData data, CancellationToken cancellationToken)
    {
        tester.Complete(identity.AgentId, data);
        return Task.CompletedTask;
    }
}

/// <summary>Lo que el recolector leyó de la API del FortiGate después de un sondeo.</summary>
public sealed class FortiGateDataHandler(IFortiGateData store, TimeProvider clock, ILogger<FortiGateDataHandler> logger)
    : TelemetryHandler<FortiGateDatosData>(logger)
{
    public override string Tipo => "fortigate_datos";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, FortiGateDatosData data, CancellationToken cancellationToken) =>
        store.SaveAsync(identity, data, clock.GetUtcNow().UtcDateTime, cancellationToken);
}
