using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Health;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Decisión 73: iniciar desde Centinela un servicio detenido (a mano desde la alerta o solo por la regla) y volver a ver
/// el comando de un token de instalación.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ServiceRepairTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private AgentTestApi _api = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.ServerConnectionString is not null)
            _api = new AgentTestApi(database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task ConfirmTwoFactorAsync(string token)
    {
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        return _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = DATEADD(MINUTE, -5, SYSUTCDATETIME()) WHERE correo = @c", ("@c", email));
    }

    private async Task<int> SantiagoAsync() => (int)(await _api.ScalarAsync(
        "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'", ("@e", Company)))!;

    private static object Spooler(string state) => new
    {
        servicios = new object[] { new { nombre = "Spooler", nombre_visible = "Cola de impresión", estado = state, inicio = "automatico", codigo_salida = state == "detenido" ? 1067 : (int?)null } },
    };

    private async Task<(RegisteredAgent Agent, System.Net.WebSockets.WebSocket Socket)> ConnectAgentAsync()
    {
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var socket = await ConnectAndGreetAsync(_api, agent);
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 30, disco = 50 });
        await _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);
        return (agent, socket);
    }

    private async Task<AlertEngineResult> EvaluateAsync(RegisteredAgent agent) =>
        await _api.Factory.Services.GetRequiredService<AlertEngine>()
            .EvaluateCompanyAsync(await database.GetCompanyIdAsync(Company), CancellationToken.None, [agent.MachineId]);

    /// <summary>El Spooler estaba corriendo y se cae con error: abre su alerta.</summary>
    private async Task StopSpoolerAsync(RegisteredAgent agent, System.Net.WebSockets.WebSocket socket)
    {
        await SendAckedAsync(socket, "servicios", Spooler("en_ejecucion"));
        await EvaluateAsync(agent);
        await SendAckedAsync(socket, "servicios", Spooler("detenido"));
        (await EvaluateAsync(agent)).Opened.Should().Be(1);
    }

    /// <summary>Manda y espera su ack, saltando los comandos que el servidor manda solo mientras tanto.</summary>
    private static async Task SendSkippingCommandsAsync(System.Net.WebSockets.WebSocket socket, string tipo, object datos)
    {
        var id = await SendAsync(socket, tipo, datos);
        for (var i = 0; i < 10; i++)
        {
            var reply = await ReceiveAsync(socket) ?? throw new InvalidOperationException("El servidor cerró la conexión.");
            if (reply.GetProperty("tipo").GetString() == "ack" && reply.GetProperty("datos").GetProperty("id").GetString() == id)
                return;
        }
        throw new InvalidOperationException($"No llegó el ack de {tipo}.");
    }

    private static async Task<JsonElement> NextCommandAsync(System.Net.WebSockets.WebSocket socket)
    {
        for (var i = 0; i < 10; i++)
        {
            var message = await ReceiveAsync(socket) ?? throw new InvalidOperationException("El servidor cerró la conexión.");
            if (message.GetProperty("tipo").GetString() == "ejecutar_comando")
                return message.GetProperty("datos");
        }
        throw new InvalidOperationException("No llegó el comando.");
    }

    [DatabaseFact]
    public async Task Starting_the_service_from_its_alert_runs_a_signed_command_and_resolves_the_alert()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var admin = _api.ClientWith(token);
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;
        await StopSpoolerAsync(agent, socket);
        var alertId = (long)(await _api.ScalarAsync(
            "SELECT id FROM dbo.alertas WHERE maquina_id = @id AND codigo = 'servicio:Spooler' AND estado <> 'resuelta'", ("@id", agent.MachineId)))!;

        // Es ejecutar un comando: pide el 2FA reciente como los scripts.
        var denied = await admin.PostAsync($"/api/v1/alertas/{alertId}/iniciar-servicio", null);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ConfirmTwoFactorAsync(token);

        var started = await JsonAsync(await admin.PostAsync($"/api/v1/alertas/{alertId}/iniciar-servicio", null));
        started.GetProperty("lote").GetGuid().Should().NotBeEmpty();
        var command = await NextCommandAsync(socket);
        command.GetProperty("contenido").GetString().Should().Be(ServiceRepairRules.Script("Spooler"));
        command.GetProperty("lenguaje").GetString().Should().Be("powershell");

        // El agente lo inicia: la alerta se resuelve sin esperar su próximo reporte de servicios.
        await SendAckedAsync(socket, "resultado_comando", new { comandoId = command.GetProperty("comando_id").GetInt64(), estado = "completado", codigoSalida = 0, salida = "Running" });
        (await _api.ScalarAsync("SELECT estado FROM dbo.alertas WHERE id = @id", ("@id", alertId))).Should().Be("resuelta");
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'alerta.iniciar_servicio' AND entidad_id = @id",
            ("@id", alertId.ToString()))).Should().Be(1);

        // Una alerta que no es de servicio no admite la acción.
        await _api.ExecuteAsync("UPDATE dbo.alertas SET estado = 'nueva', codigo = 'disco:C:' WHERE id = @id", ("@id", alertId));
        (await admin.PostAsync($"/api/v1/alertas/{alertId}/iniciar-servicio", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task With_automatic_repair_the_service_is_started_at_most_three_times_a_day()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        await JsonAsync(await admin.PutAsJsonAsync("/api/v1/reglas-alerta", new
        {
            metrica = "servicio_detenido",
            sucursalId = await SantiagoAsync(),
            duracionMin = 0,
            activa = true,
            repararAuto = true,
        }));
        var rules = await JsonAsync(await admin.GetAsync("/api/v1/reglas-alerta"));
        var serviceRule = rules.EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "servicio_detenido");
        serviceRule.GetProperty("canAutoRepair").GetBoolean().Should().BeTrue();
        serviceRule.GetProperty("overrides").EnumerateArray().Single().GetProperty("autoRepair").GetBoolean().Should().BeTrue();

        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;
        await StopSpoolerAsync(agent, socket);
        var command = await NextCommandAsync(socket);
        command.GetProperty("contenido").GetString().Should().Be(ServiceRepairRules.Script("Spooler"));
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'servicio.reparar_auto' AND entidad_id = @id",
            ("@id", agent.MachineId.ToString()))).Should().Be(1);

        // Se cae otras tres veces el mismo día: solo se intenta hasta completar tres.
        for (var i = 0; i < 3; i++)
        {
            await SendSkippingCommandsAsync(socket, "servicios", Spooler("en_ejecucion"));
            (await EvaluateAsync(agent)).Resolved.Should().Be(1);
            await SendSkippingCommandsAsync(socket, "servicios", Spooler("detenido"));
            (await EvaluateAsync(agent)).Opened.Should().Be(1);
        }
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.comandos WHERE maquina_id = @id", ("@id", agent.MachineId)))
            .Should().Be(ServiceRepairRules.MaxAutomaticPerDay);
    }

    [DatabaseFact]
    public async Task An_active_installer_token_can_be_shown_again_until_it_is_revoked()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var created = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/instaladores", new
        {
            sucursalId = await SantiagoAsync(),
            nombre = "Volver a ver",
            diasVigencia = 7,
            usosMax = 5,
            requiereAprobacion = false,
        }));
        var id = created.GetProperty("id").GetInt32();

        var list = (await JsonAsync(await admin.GetAsync("/api/v1/instaladores?limit=100"))).GetProperty("data").GetProperty("data");
        list.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id).GetProperty("canShowCommand").GetBoolean().Should().BeTrue();

        var shown = await JsonAsync(await admin.GetAsync($"/api/v1/instaladores/{id}/comando"));
        shown.GetProperty("token").GetString().Should().Be(created.GetProperty("token").GetString());
        shown.GetProperty("comando").GetString().Should().Be(created.GetProperty("comando").GetString());
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'instalador.ver_token' AND entidad_id = @id", ("@id", id.ToString())))
            .Should().Be(1);
        // En la base no queda en claro.
        (await _api.ScalarAsync("SELECT CAST(token_cifrado AS VARCHAR(MAX)) FROM dbo.tokens_registro WHERE id = @id", ("@id", id)) as string)
            .Should().NotContain(created.GetProperty("token").GetString()!);

        await JsonAsync(await admin.PostAsync($"/api/v1/instaladores/{id}/revocar", null));
        (await admin.GetAsync($"/api/v1/instaladores/{id}/comando")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
