using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Health;
using MonitoreoTI.Infrastructure.Panel;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Tickets (W-24 a W-26) por el API del panel.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class TicketApiTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
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

    private async Task<HttpClient> ClientAsync(string role, params string[] branches) =>
        _api.ClientWith(await _api.TokenForAsync(Company, role, branches));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> CreateAsync(HttpClient client, object body) =>
        (await JsonAsync(await client.PostAsJsonAsync("/api/v1/tickets", body), HttpStatusCode.Created)).GetProperty("id").GetInt32();

    /// <summary>Un equipo de Santiago con el disco lleno y su alerta abierta.</summary>
    private async Task<(RegisteredAgent Agent, long AlertId)> MachineWithAlertAsync()
    {
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var socket = await ConnectAndGreetAsync(_api, agent);
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 30, disco = 97 });
        await _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);
        await SendAckedAsync(socket, "volumenes", new { volumenes = new[] { new { unidad = "C:", total_gb = 100, libre_gb = 3 } } });
        await _api.Factory.Services.GetRequiredService<AlertEngine>()
            .EvaluateCompanyAsync(await database.GetCompanyIdAsync(Company), CancellationToken.None, [agent.MachineId]);
        var alertId = (long)(await _api.ScalarAsync("SELECT id FROM dbo.alertas WHERE maquina_id = @id", ("@id", agent.MachineId)))!;
        return (agent, alertId);
    }

    [DatabaseFact]
    public async Task A_ticket_from_an_alert_attends_it_and_cannot_be_duplicated()
    {
        var (agent, alertId) = await MachineWithAlertAsync();
        var tech = await ClientAsync("Tecnico");

        var id = await CreateAsync(tech, new { titulo = "Liberar espacio en C:", descripcion = "Disco al 97 %", prioridad = "alta", maquinaId = (int?)null, alertaId = alertId, asignadoA = (int?)null });

        var detail = await JsonAsync(await tech.GetAsync($"/api/v1/tickets/{id}"));
        detail.GetProperty("machineId").GetInt32().Should().Be(agent.MachineId, "el equipo sale de la alerta");
        detail.GetProperty("alertMessage").GetString().Should().Contain("Disco C:");
        detail.GetProperty("alertStatus").GetString().Should().Be("en_proceso", "abrir el ticket atiende la alerta");
        detail.GetProperty("status").GetString().Should().Be("abierto");
        detail.GetProperty("history")[0].GetProperty("action").GetString().Should().Be("Ticket creado");

        var again = await tech.PostAsJsonAsync("/api/v1/tickets", new { titulo = "Otra vez", prioridad = "media", alertaId = alertId });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain($"#{id}");

        var byAlert = await JsonAsync(await tech.GetAsync($"/api/v1/tickets?alerta={alertId}"));
        byAlert.GetProperty("page").GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32().Should().Be(id);
    }

    [DatabaseFact]
    public async Task The_board_flow_assigns_comments_attaches_and_closes()
    {
        var (agent, _) = await MachineWithAlertAsync();
        var tech = await ClientAsync("Tecnico");
        var id = await CreateAsync(tech, new { titulo = "Revisar disco", prioridad = "media", maquinaId = agent.MachineId });

        (await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/estado", new { estado = "en_proceso" })).EnsureSuccessStatusCode();
        var detail = await JsonAsync(await tech.GetAsync($"/api/v1/tickets/{id}"));
        detail.GetProperty("assignedTo").GetString().Should().Be("Usuario de prueba", "quien lo mueve a en proceso lo toma");
        detail.GetProperty("history").EnumerateArray().Should()
            .Contain(h => h.GetProperty("action").GetString() == "Cambio de estado" && h.GetProperty("detail").GetString() == "Abierto → En proceso");

        (await JsonAsync(await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/comentarios", new { texto = "Respaldé los datos." }), HttpStatusCode.Created))
            .GetProperty("id").GetInt32().Should().BePositive();

        var file = await JsonAsync(await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/adjuntos", new { tipo = "image/png", datos = "data:image/png;base64," + Png1x1 }), HttpStatusCode.Created);
        var fileId = file.GetProperty("id").GetInt32();
        var content = await JsonAsync(await tech.GetAsync($"/api/v1/tickets/{id}/adjuntos/{fileId}"));
        content.GetProperty("data").GetString().Should().Be(Png1x1);
        content.GetProperty("contentType").GetString().Should().Be("image/png");

        var disguised = await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/adjuntos",
            new { tipo = "image/png", datos = Convert.ToBase64String("MZ no soy una imagen"u8.ToArray()) });
        disguised.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/estado", new { estado = "cerrado" })).EnsureSuccessStatusCode();
        detail = await JsonAsync(await tech.GetAsync($"/api/v1/tickets/{id}"));
        detail.GetProperty("closedAt").ValueKind.Should().Be(JsonValueKind.String);
        detail.GetProperty("comments").EnumerateArray().Single().GetProperty("text").GetString().Should().Be("Respaldé los datos.");
        detail.GetProperty("attachments").GetArrayLength().Should().Be(1);
        (await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/comentarios", new { texto = "Tarde" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var active = await JsonAsync(await tech.GetAsync($"/api/v1/tickets?maquina={agent.MachineId}"));
        active.GetProperty("page").GetProperty("total").GetInt32().Should().Be(0, "la lista por defecto oculta los cerrados");
    }

    [DatabaseFact]
    public async Task An_employee_only_sees_and_edits_their_own_tickets()
    {
        var (agent, _) = await MachineWithAlertAsync();
        var tech = await ClientAsync("Tecnico");
        var employee = await ClientAsync("Empleado");

        var techTicket = await CreateAsync(tech, new { titulo = "Ticket del técnico", prioridad = "baja", maquinaId = agent.MachineId });
        var mine = await CreateAsync(employee, new { titulo = "Mi impresora no imprime", prioridad = "media", maquinaId = agent.MachineId });

        var list = await JsonAsync(await employee.GetAsync("/api/v1/tickets?estado=todos&tamano=300"));
        list.GetProperty("page").GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetInt32()).Should().Equal(mine);
        (await employee.GetAsync($"/api/v1/tickets/{techTicket}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await employee.PostAsJsonAsync($"/api/v1/tickets/{mine}/estado", new { estado = "cerrado" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await employee.PostAsJsonAsync("/api/v1/tickets", new { titulo = "Asignado", prioridad = "media", asignadoA = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await employee.PutAsJsonAsync($"/api/v1/tickets/{mine}", new { titulo = "Mi impresora no imprime nada", prioridad = "alta" }))
            .EnsureSuccessStatusCode();

        // Ya tomado por un técnico, el autor no puede cambiarlo.
        (await tech.PostAsJsonAsync($"/api/v1/tickets/{mine}/estado", new { estado = "en_proceso" })).EnsureSuccessStatusCode();
        (await employee.PutAsJsonAsync($"/api/v1/tickets/{mine}", new { titulo = "Cambio", prioridad = "urgente" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task Branch_scope_and_technician_list_are_enforced()
    {
        var (agent, _) = await MachineWithAlertAsync();
        var tech = await ClientAsync("Tecnico");
        var id = await CreateAsync(tech, new { titulo = "En Santiago", prioridad = "media", maquinaId = agent.MachineId });

        var otherBranch = await ClientAsync("Tecnico", "Santo Domingo");
        (await otherBranch.GetAsync($"/api/v1/tickets/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherBranch.PostAsJsonAsync($"/api/v1/tickets/{id}/estado", new { estado = "cerrado" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherBranch.PostAsJsonAsync("/api/v1/tickets", new { titulo = "Ajeno", prioridad = "media", maquinaId = agent.MachineId }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var technicians = await JsonAsync(await tech.GetAsync("/api/v1/tickets/tecnicos"));
        var supervisorId = (int)(await _api.ScalarAsync("""
            SELECT TOP 1 ue.usuario_id FROM dbo.usuario_empresa ue JOIN dbo.roles r ON r.id = ue.rol_id
            JOIN dbo.empresas e ON e.id = ue.empresa_id WHERE e.nombre = @e AND r.nombre = 'Supervisor'
            """, ("@e", Company)) ?? 0);
        if (supervisorId > 0)
        {
            technicians.EnumerateArray().Should().NotContain(t => t.GetProperty("id").GetInt32() == supervisorId);
            (await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/asignar", new { usuarioId = supervisorId }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, "un supervisor no gestiona tickets");
        }
        var techId = technicians.EnumerateArray().First().GetProperty("id").GetInt32();
        (await tech.PostAsJsonAsync($"/api/v1/tickets/{id}/asignar", new { usuarioId = techId })).EnsureSuccessStatusCode();
        (await JsonAsync(await tech.GetAsync($"/api/v1/tickets/{id}"))).GetProperty("assignedToId").GetInt32().Should().Be(techId);
    }

    [Theory]
    [InlineData("image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "png")]
    [InlineData("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData("image/jpeg", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, null)]
    [InlineData("image/webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData("image/gif", new byte[] { 0x47, 0x49, 0x46 }, null)]
    public void Attachments_must_match_their_declared_type(string type, byte[] content, string? extension) =>
        TicketCommands.ImageExtension(type, content).Should().Be(extension);
}
