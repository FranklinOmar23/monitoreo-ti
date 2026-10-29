using System.Net;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// El panel corre en un navegador (Electron): antes de cada POST con JSON el navegador pregunta (preflight)
/// si el origen está permitido. Sin estas cabeceras el login falla aunque el API responda bien a curl.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CorsTests(TestDatabase database)
{
    private const string PanelOrigin = "http://localhost:5173";

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        return request;
    }

    [DatabaseFact]
    public async Task The_panel_origin_passes_the_preflight()
    {
        await using var api = new AgentTestApi(database, new() { ["Cors:PanelOrigins:0"] = PanelOrigin });

        var response = await api.Client.SendAsync(Preflight(PanelOrigin));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(PanelOrigin);
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse("la sesión no usa cookies");
    }

    [DatabaseFact]
    public async Task The_realtime_hub_negotiation_passes_the_preflight()
    {
        await using var api = new AgentTestApi(database, new() { ["Cors:PanelOrigins:0"] = PanelOrigin });
        var request = new HttpRequestMessage(HttpMethod.Options, "/hubs/tiempo-real/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", PanelOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        // Cabeceras que envía @microsoft/signalr en el navegador.
        request.Headers.Add("Access-Control-Request-Headers", "authorization,x-requested-with,x-signalr-user-agent");

        var response = await api.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        string.Join(',', response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant()
            .Should().Contain("x-signalr-user-agent").And.Contain("x-requested-with");
    }

    [DatabaseFact]
    public async Task Other_origins_are_not_allowed()
    {
        await using var api = new AgentTestApi(database, new() { ["Cors:PanelOrigins:0"] = PanelOrigin });

        var response = await api.Client.SendAsync(Preflight("https://sitio-malicioso.example"));

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
