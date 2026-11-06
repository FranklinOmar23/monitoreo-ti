using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Remote;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Fase 6.1: scripts y comandos firmados. El servidor firma con la llave de la empresa, exige un 2FA reciente y el
/// módulo remoto; el agente recibe "ejecutar_comando" con una firma que se verifica con la llave de su bienvenida.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RemoteCommandsTests(TestDatabase database) : IAsyncLifetime
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

    /// <summary>Simula que el usuario del token confirmó su 2FA hace un rato.</summary>
    private Task ConfirmTwoFactorAsync(string token, TimeSpan ago)
    {
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        return _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = DATEADD(MINUTE, -@m, SYSUTCDATETIME()) WHERE correo = @c",
            ("@m", (int)ago.TotalMinutes), ("@c", email));
    }

    [DatabaseFact]
    public async Task A_signed_script_reaches_the_agent_and_its_output_comes_back()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var admin = _api.ClientWith(token);
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");

        // Biblioteca: se guarda firmada.
        var name = $"Prueba {Guid.NewGuid():N}";
        var created = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/scripts", new
        {
            nombre = name,
            lenguaje = "powershell",
            descripcion = "Limpia temporales",
            contenido = "Write-Output 'hola'\r\nGet-Date",
        }), HttpStatusCode.Created);
        var scriptId = created.GetProperty("id").GetInt32();
        var scripts = (await JsonAsync(await admin.GetAsync("/api/v1/scripts?limit=100"))).GetProperty("data").GetProperty("data");
        scripts.EnumerateArray().Single(s => s.GetProperty("id").GetInt32() == scriptId).GetProperty("signed").GetBoolean().Should().BeTrue();

        using var socket = await ConnectAsync(_api, agent.Credential);
        await SendAsync(socket, "hola", new { version = "0.1.0", hostname = "PC", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        var welcome = (await ReceiveAsync(socket))!.Value.GetProperty("datos");
        var publicKey = welcome.GetProperty("llave_empresa").GetString()!;

        // Sin 2FA reciente se pide el código (decisión 52).
        var run = new { maquinaIds = new[] { agent.MachineId }, scriptId, timeoutS = 60 };
        var denied = await admin.PostAsJsonAsync("/api/v1/ejecuciones", run);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString().Should().Be("2fa_requerido");
        await ConfirmTwoFactorAsync(token, TimeSpan.FromHours(9));
        (await admin.PostAsJsonAsync("/api/v1/ejecuciones", run)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await ConfirmTwoFactorAsync(token, TimeSpan.FromMinutes(5));
        var started = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/ejecuciones", run), HttpStatusCode.Created);
        var batchId = started.GetProperty("batchId").GetGuid();

        // El agente conectado lo recibe al momento, firmado para su equipo.
        var message = (await ReceiveAsync(socket))!.Value;
        message.GetProperty("tipo").GetString().Should().Be("ejecutar_comando");
        var d = message.GetProperty("datos");
        var commandId = d.GetProperty("comando_id").GetInt64();
        d.GetProperty("contenido").GetString().Should().Be("Write-Output 'hola'\nGet-Date");
        var payload = CommandSigning.CommandPayload(commandId, agent.MachineId, d.GetProperty("tipo").GetString()!, d.GetProperty("lenguaje").GetString()!,
            d.GetProperty("timeout_s").GetInt32(), d.GetProperty("vence").GetString()!, d.GetProperty("contenido").GetString()!);
        CompanySigner.Verify(publicKey, payload, d.GetProperty("firma").GetString()!).Should().BeTrue();
        // La misma firma no sirve para otro equipo.
        var other = CommandSigning.CommandPayload(commandId, agent.MachineId + 1, "script", "powershell", 60, d.GetProperty("vence").GetString()!, d.GetProperty("contenido").GetString()!);
        CompanySigner.Verify(publicKey, other, d.GetProperty("firma").GetString()!).Should().BeFalse();

        await SendAckedAsync(socket, "resultado_comando", new { comandoId = commandId, estado = "ejecutando" });
        await SendAckedAsync(socket, "resultado_comando", new { comandoId = commandId, estado = "completado", codigoSalida = 0, salida = "hola\n30/10/2026" });
        // Un "ejecutando" atrasado no deshace el final.
        await SendAckedAsync(socket, "resultado_comando", new { comandoId = commandId, estado = "ejecutando" });

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/ejecuciones/{batchId}"));
        detail.GetProperty("summary").GetProperty("title").GetString().Should().Be(name);
        detail.GetProperty("summary").GetProperty("completed").GetInt32().Should().Be(1);
        var item = detail.GetProperty("commands").EnumerateArray().Single();
        item.GetProperty("status").GetString().Should().Be("completado");
        item.GetProperty("exitCode").GetInt32().Should().Be(0);
        item.GetProperty("output").GetString().Should().Contain("hola");

        // La auditoría guarda el hash, no el contenido.
        var audit = (string)(await _api.ScalarAsync(
            "SELECT TOP 1 detalle FROM dbo.auditoria WHERE accion = 'comando.ejecutar' AND entidad_id = @l", ("@l", batchId.ToString())))!;
        audit.Should().Contain(CommandSigning.Sha256Hex("Write-Output 'hola'\nGet-Date")).And.NotContain("Write-Output");
    }

    [DatabaseFact]
    public async Task A_disconnected_agent_gets_its_command_on_the_next_welcome_and_a_rejection_is_audited()
    {
        var token = await _api.TokenForAsync(Company, "Tecnico");
        var tech = _api.ClientWith(token);
        await ConfirmTwoFactorAsync(token, TimeSpan.FromMinutes(1));
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");

        // El técnico ejecuta comandos pero no edita la biblioteca.
        (await tech.PostAsJsonAsync("/api/v1/scripts", new { nombre = "x", lenguaje = "cmd", contenido = "dir" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await JsonAsync(await tech.PostAsJsonAsync("/api/v1/ejecuciones", new
        {
            maquinaIds = new[] { agent.MachineId },
            lenguaje = "cmd",
            contenido = "ipconfig /all",
        }), HttpStatusCode.Created);

        using var socket = await ConnectAndGreetAsync(_api, agent);
        var message = (await ReceiveAsync(socket))!.Value;
        message.GetProperty("tipo").GetString().Should().Be("ejecutar_comando");
        var commandId = message.GetProperty("datos").GetProperty("comando_id").GetInt64();

        await SendAckedAsync(socket, "resultado_comando", new { comandoId = commandId, estado = "rechazado", error = "firma no válida" });

        var status = (string)(await _api.ScalarAsync("SELECT estado FROM dbo.comandos WHERE id = @i", ("@i", commandId)))!;
        status.Should().Be("error");
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'comando.rechazado' AND entidad_id = @i",
            ("@i", commandId.ToString()))).Should().Be(1);
    }

    [DatabaseFact]
    public async Task A_plan_without_the_remote_module_cannot_run_commands_even_calling_the_api()
    {
        var token = await _api.TokenForAsync("Clínica San Rafael", "Administrador");
        await ConfirmTwoFactorAsync(token, TimeSpan.FromMinutes(1));
        var agent = await _api.RegisterNewAgentAsync("Clínica San Rafael", (string)(await _api.ScalarAsync(
            "SELECT TOP 1 s.nombre FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = N'Clínica San Rafael'"))!);

        var response = await _api.ClientWith(token).PostAsJsonAsync("/api/v1/ejecuciones", new
        {
            maquinaIds = new[] { agent.MachineId },
            lenguaje = "powershell",
            contenido = "Get-Process",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString().Should().Be("modulo_no_incluido");
    }
}
