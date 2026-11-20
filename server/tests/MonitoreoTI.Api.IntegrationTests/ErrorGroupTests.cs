using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Infrastructure.Agents;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Decisión 91: los errores del equipo agrupados, resueltos, ignorados y reparados desde Centinela.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ErrorGroupTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private const string EnergyMessage = "The Energy Server Service queencreek service terminated unexpectedly. It has done this 105 time(s).";
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

    private static object Event(DateTime at, string source, int code, string message, long id) =>
        new { registro = "System", id_registro = id, fecha = at, origen = source, codigo = code, nivel = "error", mensaje = message };

    private static JsonElement Group(JsonElement list, string source, int code) =>
        list.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("source").GetString() == source && g.GetProperty("code").GetInt32() == code);

    private Task ConfirmTwoFactorAsync(string token)
    {
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        return _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = DATEADD(MINUTE, -5, SYSUTCDATETIME()) WHERE correo = @c", ("@c", email));
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
    public async Task Errors_are_grouped_resolved_ignored_and_repaired_from_the_panel()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var admin = _api.ClientWith(token);
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var socket = await ConnectAndGreetAsync(_api, agent);
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 30, disco = 50 });
        await _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);
        await SendAckedAsync(socket, "servicios", new
        {
            servicios = new object[]
            {
                new { nombre = "MSSQLSERVER", nombre_visible = "SQL Server (MSSQLSERVER)", estado = "en_ejecucion", inicio = "automatico" },
                new { nombre = "EnergyServer", nombre_visible = "The Energy Server Service queencreek", estado = "en_ejecucion", inicio = "automatico" },
            },
        });
        var now = DateTime.UtcNow;
        await SendAckedAsync(socket, "eventos", new
        {
            eventos = new[]
            {
                Event(now.AddMinutes(-30), "MSSQLSERVER", 701, "There is insufficient system memory in resource pool 'default' to run this query.", 1),
                Event(now.AddMinutes(-20), "MSSQLSERVER", 701, "There is insufficient system memory in resource pool 'default' to run this query.", 2),
                Event(now.AddMinutes(-10), "MSSQLSERVER", 701, "There is insufficient system memory in resource pool 'default' to run this query.", 3),
                Event(now.AddMinutes(-25), "Service Control Manager", 7034, EnergyMessage, 4),
                Event(now.AddMinutes(-5), "Service Control Manager", 7034, EnergyMessage, 5),
                Event(now.AddMinutes(-15), "Microsoft-Windows-DistributedCOM", 10016, "The application-specific permission settings do not grant…", 6),
            },
        });
        var errors = $"/api/v1/maquinas/{agent.MachineId}/errores";

        // Agrupados: 6 errores en 3 grupos, con su diagnóstico y la acción que corresponde.
        var list = await JsonAsync(await admin.GetAsync($"{errors}?dias=7"));
        list.GetProperty("openTotal").GetInt32().Should().Be(6);
        list.GetProperty("groups").GetArrayLength().Should().Be(3);
        var sql = Group(list, "MSSQLSERVER", 701);
        sql.GetProperty("count").GetInt32().Should().Be(3);
        sql.GetProperty("title").GetString().Should().Be("SQL Server se quedó sin memoria");
        sql.GetProperty("service").GetString().Should().Be("MSSQLSERVER");
        Group(list, "Service Control Manager", 7034).GetProperty("service").GetString().Should().Be("EnergyServer");
        Group(list, "Microsoft-Windows-DistributedCOM", 10016).GetProperty("noise").GetBoolean().Should().BeTrue();
        Group(list, "Microsoft-Windows-DistributedCOM", 10016).GetProperty("service").ValueKind.Should().Be(JsonValueKind.Null);

        // Resuelto: deja de contar (también en la insignia de la ficha)…
        await JsonAsync(await admin.PutAsJsonAsync($"{errors}/estado", new { origen = "MSSQLSERVER", codigo = 701, estado = "resuelto", nota = "Se limitó la memoria" }));
        list = await JsonAsync(await admin.GetAsync(errors));
        Group(list, "MSSQLSERVER", 701).GetProperty("state").GetString().Should().Be(ErrorGroupRules.Resolved);
        Group(list, "MSSQLSERVER", 701).GetProperty("stateNote").GetString().Should().Be("Se limitó la memoria");
        list.GetProperty("openTotal").GetInt32().Should().Be(3);
        (await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{agent.MachineId}"))).GetProperty("openErrorCount").GetInt32().Should().Be(3);

        // …y si vuelve a ocurrir, reaparece con solo lo nuevo.
        await Task.Delay(1_100);
        await SendAckedAsync(socket, "eventos", new
        {
            eventos = new[] { Event(DateTime.UtcNow, "MSSQLSERVER", 701, "There is insufficient system memory in resource pool 'default' to run this query.", 7) },
        });
        list = await JsonAsync(await admin.GetAsync(errors));
        Group(list, "MSSQLSERVER", 701).GetProperty("state").GetString().Should().Be(ErrorGroupRules.Reappeared);
        Group(list, "MSSQLSERVER", 701).GetProperty("openCount").GetInt32().Should().Be(1);

        // Resolver vale solo en el equipo; ignorar en toda la empresa sí, y se puede deshacer.
        (await admin.PutAsJsonAsync($"{errors}/estado", new { origen = "MSSQLSERVER", codigo = 701, estado = "resuelto", alcance = "empresa" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await JsonAsync(await admin.PutAsJsonAsync($"{errors}/estado",
            new { origen = "Microsoft-Windows-DistributedCOM", codigo = 10016, estado = "ignorado", alcance = "empresa" }));
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.errores_estado WHERE maquina_id IS NULL AND codigo = 10016 AND ignorado = 1")).Should().Be(1);
        list = await JsonAsync(await admin.GetAsync(errors));
        Group(list, "Microsoft-Windows-DistributedCOM", 10016).GetProperty("stateScope").GetString().Should().Be("empresa");
        await JsonAsync(await admin.PostAsJsonAsync($"{errors}/reabrir", new { origen = "Microsoft-Windows-DistributedCOM", codigo = 10016, alcance = "empresa" }));
        list = await JsonAsync(await admin.GetAsync(errors));
        Group(list, "Microsoft-Windows-DistributedCOM", 10016).GetProperty("state").GetString().Should().Be(ErrorGroupRules.Open);

        // Las ocurrencias de un grupo.
        (await JsonAsync(await admin.GetAsync($"{errors}/ocurrencias?origen=MSSQLSERVER&codigo=701"))).GetArrayLength().Should().Be(4);

        // Reparar: un comando firmado que reinicia el servicio que el servidor encontró; pide el 2FA reciente.
        var fix = new { origen = "Service Control Manager", codigo = 7034 };
        (await admin.PostAsJsonAsync($"{errors}/reparar", fix)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await ConfirmTwoFactorAsync(token);
        var repaired = await JsonAsync(await admin.PostAsJsonAsync($"{errors}/reparar", fix));
        repaired.GetProperty("servicio").GetString().Should().Be("EnergyServer");
        var command = await NextCommandAsync(socket);
        command.GetProperty("contenido").GetString().Should().Be(ServiceRepairRules.RestartScript("EnergyServer"));
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'errores.reparar' AND entidad_id = @id",
            ("@id", agent.MachineId.ToString()))).Should().Be(1);

        // Un error sin reparación no la admite.
        (await admin.PostAsJsonAsync($"{errors}/reparar", new { origen = "Microsoft-Windows-DistributedCOM", codigo = 10016 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
