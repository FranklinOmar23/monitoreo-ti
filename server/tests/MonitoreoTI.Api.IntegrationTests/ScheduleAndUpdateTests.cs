using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Remote;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Fase 6.5 (tareas programadas) y 6.6 (actualización automática del agente).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduleAndUpdateTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private static readonly ECDsa ProductKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    // Cualquier binario con versión sirve de "instalador publicado" en la prueba.
    private static readonly string Installer = typeof(ScheduleAndUpdateTests).Assembly.Location;
    private AgentTestApi _api = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.ServerConnectionString is not null)
        {
            _api = new AgentTestApi(database, new Dictionary<string, string>
            {
                ["Instaladores:Archivo"] = Installer,
                ["Actualizaciones:LlavePrivada"] = Convert.ToBase64String(ProductKey.ExportPkcs8PrivateKey()),
            });
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
        {
            await _api.ExecuteAsync("DELETE t FROM dbo.tareas_programadas t JOIN dbo.usuarios u ON u.id = t.creado_por WHERE u.correo LIKE '%@prueba.test'");
            await _api.ExecuteAsync("UPDATE dbo.empresas SET actualizacion_automatica = 1");
            await _api.DisposeAsync();
        }
    }

    private async Task<HttpClient> AdminAsync()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = SYSUTCDATETIME() WHERE correo = @c", ("@c", email));
        return _api.ClientWith(token);
    }

    [DatabaseFact]
    public async Task A_due_task_creates_signed_commands_for_its_machines_and_a_one_time_task_then_pauses()
    {
        var admin = await AdminAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var script = (await (await admin.PostAsJsonAsync("/api/v1/scripts", new
        {
            nombre = $"Prueba tarea {Guid.NewGuid():N}",
            lenguaje = "powershell",
            contenido = "Clear-RecycleBin -Force",
        })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-4)).AddDays(1);
        var created = await admin.PostAsJsonAsync("/api/v1/tareas-programadas", new
        {
            nombre = $"Papelera {Guid.NewGuid():N}",
            scriptId = script,
            objetivo = "equipos",
            maquinaIds = new[] { agent.MachineId },
            frecuencia = "una_vez",
            hora = "03:00",
            fecha = tomorrow,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var taskId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var list = (await (await admin.GetAsync("/api/v1/tareas-programadas?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        var item = list.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == taskId);
        item.GetProperty("nextRunAt").GetDateTime().Should().Be(tomorrow.ToDateTime(new TimeOnly(7, 0)));
        item.GetProperty("description").GetString().Should().StartWith("Una vez");

        // El script de una tarea no se puede borrar.
        (await admin.DeleteAsync($"/api/v1/scripts/{script}")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Le toca ahora: el agente conectado recibe el comando firmado (sin ofertas de actualización de por medio).
        await _api.ExecuteAsync("UPDATE dbo.empresas SET actualizacion_automatica = 0");
        await _api.ExecuteAsync("UPDATE dbo.tareas_programadas SET proxima = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE id = @i", ("@i", taskId));
        using var socket = await ConnectAndGreetAsync(_api, agent);
        var companyId = await database.GetCompanyIdAsync(Company);
        (await _api.Factory.Services.GetRequiredService<ScheduledTaskRunner>().RunDueAsync(companyId, CancellationToken.None)).Should().BeGreaterThan(0);

        var message = (await ReceiveAsync(socket))!.Value;
        message.GetProperty("tipo").GetString().Should().Be("ejecutar_comando");
        message.GetProperty("datos").GetProperty("contenido").GetString().Should().Be("Clear-RecycleBin -Force");

        var row = (await (await admin.GetAsync("/api/v1/tareas-programadas?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        var ran = row.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == taskId);
        ran.GetProperty("active").GetBoolean().Should().BeFalse();
        ran.GetProperty("lastBatchId").ValueKind.Should().Be(JsonValueKind.String);
    }

    [DatabaseFact]
    public async Task An_outdated_agent_is_offered_the_signed_release_and_can_download_it_with_its_credential()
    {
        var admin = await AdminAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var version = FileVersionInfo.GetVersionInfo(Installer).ProductVersion!;

        using var socket = await ConnectAsync(_api, agent.Credential);
        await SendAsync(socket, "hola", new { version = "0.0.1", hostname = "PC", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        await ReceiveAsync(socket); // bienvenida
        var offer = (await ReceiveAsync(socket))!.Value;
        offer.GetProperty("tipo").GetString().Should().Be("actualizar_agente");
        var d = offer.GetProperty("datos");
        d.GetProperty("version").GetString().Should().Be(version);

        // La firma es de la llave del producto sobre la versión y el hash del archivo.
        var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Installer))).ToLowerInvariant();
        d.GetProperty("sha256").GetString().Should().Be(sha);
        ProductKey.VerifyData(Encoding.UTF8.GetBytes(UpdateSigning.Payload(version, sha)), Convert.FromBase64String(d.GetProperty("firma").GetString()!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation).Should().BeTrue();

        // Descarga: solo con la credencial del agente.
        (await _api.Client.GetAsync(d.GetProperty("url").GetString())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var download = new HttpRequestMessage(HttpMethod.Get, d.GetProperty("url").GetString());
        download.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agent.Credential);
        var file = await _api.Client.SendAsync(download);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        Convert.ToHexString(SHA256.HashData(await file.Content.ReadAsByteArrayAsync())).ToLowerInvariant().Should().Be(sha);

        // El panel lo ve atrasado y "actualizando".
        var status = await (await admin.GetAsync("/api/v1/actualizaciones")).Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("available").GetString().Should().Be(version);
        status.GetProperty("outdatedAgents").EnumerateArray().Should().Contain(a =>
            a.GetProperty("machineId").GetInt32() == agent.MachineId && a.GetProperty("updating").GetBoolean());
    }

    [DatabaseFact]
    public async Task With_automatic_updates_off_the_agent_is_not_offered_anything_until_the_admin_deploys()
    {
        var admin = await AdminAsync();
        (await admin.PutAsJsonAsync("/api/v1/actualizaciones/automatica", new { activa = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");

        using var socket = await ConnectAsync(_api, agent.Credential);
        await SendAsync(socket, "hola", new { version = "0.0.1", hostname = "PC", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        await ReceiveAsync(socket); // bienvenida
        await SendAckedAsync(socket, "heartbeat", new { }); // nada más llegó antes del ack

        var deployed = await (await admin.PostAsync("/api/v1/actualizaciones/desplegar", null)).Content.ReadFromJsonAsync<JsonElement>();
        deployed.GetProperty("ofrecidos").GetInt32().Should().BeGreaterThan(0);
        (await ReceiveAsync(socket))!.Value.GetProperty("tipo").GetString().Should().Be("actualizar_agente");
    }
}
