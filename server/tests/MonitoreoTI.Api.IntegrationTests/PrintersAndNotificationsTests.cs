using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Infrastructure.Health;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Cierre de la Fase 3: tóner de impresoras por SNMP (v2c o v3), correo de alertas críticas con su preferencia por
/// usuario y calendario de mantenimiento (W-27).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PrintersAndNotificationsTests(TestDatabase database) : IAsyncLifetime
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

    private async Task<int> SantiagoAsync() => (int)(await _api.ScalarAsync(
        "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'", ("@e", Company)))!;

    [DatabaseFact]
    public async Task A_printer_reports_its_toner_and_a_low_level_opens_an_alert_that_is_mailed_once()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();
        var collector = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var name = $"PRUEBA-IMP-{Random.Shared.Next(100000)}";

        // Comunidad v2c (decisión 47): se guarda cifrada y nunca se devuelve.
        var created = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/maquinas", new
        {
            nombre = name,
            tipo = "impresora",
            sucursalId = branch,
            ip = "10.30.20.90",
            snmpVersion = "v2c",
            snmpComunidad = "lectura-123",
            recolectorId = collector.AgentId,
        }), HttpStatusCode.Created);
        var printer = created.GetProperty("id").GetInt32();

        using var socket = await ConnectAsync(_api, collector.Credential);
        await SendAsync(socket, "hola", new { version = "0.1.0", hostname = "RECOLECTOR", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        var welcome = (await ReceiveAsync(socket))!.Value.GetProperty("datos");
        var item = welcome.GetProperty("recolector").GetProperty("dispositivos").EnumerateArray().Single();
        item.GetProperty("origen").GetString().Should().Be("impresora");
        item.GetProperty("version").GetString().Should().Be("v2c");
        SnmpCredentialSeal.Open(collector.Credential, item.GetProperty("credencial").GetString()!).Should().Contain("lectura-123");

        await SendAckedAsync(socket, "snmp_resultado", new
        {
            maquinaId = printer,
            ok = true,
            consumibles = new object[]
            {
                new { indice = "1.1", descripcion = "Tóner negro", nivel = 3, maximo = 100, clase = 3 },
                new { indice = "1.2", descripcion = "Depósito de residuos", nivel = 80, maximo = 100, clase = 4 },
            },
        });

        var raw = await (await admin.GetAsync($"/api/v1/maquinas/{printer}")).Content.ReadAsStringAsync();
        raw.Should().NotContain("lectura-123");
        var detail = JsonDocument.Parse(raw).RootElement;
        detail.GetProperty("snmp").GetProperty("version").GetString().Should().Be("v2c");
        detail.GetProperty("snmp").GetProperty("collectorId").GetInt32().Should().Be(collector.AgentId);
        var toner = detail.GetProperty("components").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "toner").ToList();
        toner.Single(c => c.GetProperty("description").GetString() == "Tóner negro").GetProperty("capacity").GetString().Should().Be("3 %");
        toner.Single(c => c.GetProperty("description").GetString() == "Depósito de residuos").GetProperty("capacity").ValueKind.Should().Be(JsonValueKind.Null);

        var companyId = await database.GetCompanyIdAsync(Company);
        await _api.Factory.Services.GetRequiredService<AlertEngine>().EvaluateCompanyAsync(companyId, CancellationToken.None, [printer]);
        var problems = (await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{printer}"))).GetProperty("problems").EnumerateArray().ToList();
        problems.Single().GetProperty("message").GetString().Should().Be("Tóner negro al 3 %");

        // Correo de la crítica: una sola vez, con el nombre de la impresora; quien lo desactivó no lo recibe.
        var quiet = await _api.TokenForAsync(Company, "Administrador");
        var quietClient = _api.ClientWith(quiet);
        (await JsonAsync(await quietClient.GetAsync("/api/v1/auth/preferencias"))).GetProperty("notificarCorreo").GetBoolean().Should().BeTrue();
        (await quietClient.PutAsJsonAsync("/api/v1/auth/preferencias", new { notificarCorreo = false })).EnsureSuccessStatusCode();
        var quietEmail = (string)(await _api.ScalarAsync("SELECT TOP 1 correo FROM dbo.usuarios WHERE notificar_correo = 0 AND correo LIKE '%@prueba.test'"))!;

        var mailer = _api.Factory.Services.GetRequiredService<AlertMailer>();
        (await mailer.NotifyCompanyAsync(companyId, CancellationToken.None)).Should().BeGreaterThan(0);
        var mails = Directory.GetFiles(TestHost.EmailFolder).Select(File.ReadAllText).Where(t => t.Contains(name)).ToList();
        mails.Should().NotBeEmpty();
        mails.Should().AllSatisfy(m => m.Should().Contain("Tóner negro al 3 %").And.Contain("Qué hacer"));
        mails.Should().NotContain(m => m.Contains($"Para: {quietEmail}"));
        await mailer.NotifyCompanyAsync(companyId, CancellationToken.None);
        Directory.GetFiles(TestHost.EmailFolder).Select(File.ReadAllText).Count(t => t.Contains(name)).Should().Be(mails.Count, "cada alerta se avisa una vez");
    }

    [DatabaseFact]
    public async Task Snmp_settings_are_validated()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();

        var notPrinter = await admin.PostAsJsonAsync("/api/v1/maquinas", new { nombre = "PRUEBA-PC-SNMP", tipo = "computadora", sucursalId = branch, ip = "10.1.1.1", snmpVersion = "v2c", snmpComunidad = "x" });
        notPrinter.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var noCommunity = await admin.PostAsJsonAsync("/api/v1/maquinas", new
        {
            nombre = "PRUEBA-IMP-SIN",
            tipo = "impresora",
            sucursalId = branch,
            ip = "10.1.1.2",
            snmpVersion = "v2c",
        });
        noCommunity.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noCommunity.Content.ReadAsStringAsync()).Should().Contain("comunidad");

        var foreign = await _api.RegisterNewAgentAsync(Company, "Santo Domingo");
        var wrongCollector = await admin.PostAsJsonAsync("/api/v1/maquinas", new
        {
            nombre = "PRUEBA-IMP-LEJOS",
            tipo = "impresora",
            sucursalId = branch,
            ip = "10.1.1.3",
            snmpVersion = "v2c",
            snmpComunidad = "lectura",
            recolectorId = foreign.AgentId,
        });
        wrongCollector.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task The_maintenance_calendar_programs_marks_done_and_keeps_history()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();
        var machine = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/maquinas", new { nombre = $"PRUEBA-MANT-{Random.Shared.Next(100000)}", tipo = "computadora", sucursalId = branch }),
            HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var late = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/mantenimientos", new
        {
            maquinaId = machine,
            tipo = "preventivo",
            descripcion = "Limpieza interna",
            fechaProgramada = today.AddDays(-2),
        }), HttpStatusCode.Created)).GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync("/api/v1/mantenimientos", new { maquinaId = machine, tipo = "preventivo", fechaProgramada = today.AddDays(5) }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await admin.PostAsJsonAsync("/api/v1/mantenimientos", new { maquinaId = machine, tipo = "correctivo" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "hace falta una fecha");

        var list = await JsonAsync(await admin.GetAsync($"/api/v1/mantenimientos?desde={today.AddDays(-7):yyyy-MM-dd}&hasta={today.AddDays(7):yyyy-MM-dd}&maquina={machine}"));
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Should().Equal("vencido", "programado");
        list.GetProperty("overdue").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        (await admin.PostAsJsonAsync($"/api/v1/mantenimientos/{late}/realizado", new { nota = "Se cambió la pasta térmica" })).EnsureSuccessStatusCode();
        list = await JsonAsync(await admin.GetAsync($"/api/v1/mantenimientos?desde={today.AddDays(-7):yyyy-MM-dd}&hasta={today.AddDays(7):yyyy-MM-dd}&maquina={machine}"));
        list.GetProperty("items").EnumerateArray().First().GetProperty("status").GetString().Should().Be("realizado");
        (await admin.DeleteAsync($"/api/v1/mantenimientos/{late}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var history = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{machine}/historial"));
        history.GetRawText().Should().Contain("mantenimiento.realizar");

        // Sin tickets.gestionar se ve, pero no se programa.
        var support = _api.ClientWith(await _api.TokenForAsync(Company, "Supervisor"));
        (await support.GetAsync($"/api/v1/mantenimientos?maquina={machine}")).StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Forbidden);
        (await support.PostAsJsonAsync("/api/v1/mantenimientos", new { maquinaId = machine, tipo = "preventivo", fechaProgramada = today }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
