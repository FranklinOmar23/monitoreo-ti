using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Enlaces monitoreoti://q/{codigo}: el código dice qué es, solo si el usuario puede verlo.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class QrResolveTests(TestDatabase database) : IAsyncLifetime
{
    private const string Caribe = "Grupo Comercial Caribe";
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

    [DatabaseFact]
    public async Task A_machine_code_resolves_only_for_who_sees_its_branch()
    {
        var agent = await _api.RegisterNewAgentAsync(Caribe, "Santiago");
        var code = (Guid)(await _api.ScalarAsync("SELECT codigo_qr FROM dbo.maquinas WHERE id = @id", ("@id", agent.MachineId)))!;

        var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var target = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/qr/{code}");
        target.GetProperty("type").GetString().Should().Be("maquina");
        target.GetProperty("id").GetInt32().Should().Be(agent.MachineId);

        var otherBranch = _api.ClientWith(await _api.TokenForAsync(Caribe, "Tecnico", "Santo Domingo"));
        (await otherBranch.GetAsync($"/api/v1/qr/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/v1/qr/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // De otra empresa, ni siquiera se sabe que existe.
        var other = _api.ClientWith(await _api.TokenForAsync("Clínica San Rafael", "Administrador"));
        (await other.GetAsync($"/api/v1/qr/{code}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DatabaseFact]
    public async Task The_panel_updates_are_served_without_a_session_but_only_the_release_files()
    {
        var folder = Directory.CreateTempSubdirectory("panel-").FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "latest.yml"), "version: 0.2.0\n");
        await File.WriteAllBytesAsync(Path.Combine(folder, "MonitoreoTI-Panel-Setup-0.2.0.exe"), [0x4D, 0x5A, 1, 2]);
        await File.WriteAllTextAsync(Path.Combine(folder, "secreto.txt"), "no");
        await using var api = new AgentTestApi(database, new() { ["PanelActualizaciones:Carpeta"] = folder });

        var latest = await api.Client.GetAsync("/api/v1/panel/actualizacion/latest.yml");
        latest.StatusCode.Should().Be(HttpStatusCode.OK);
        (await latest.Content.ReadAsStringAsync()).Should().Contain("0.2.0");
        (await api.Client.GetByteArrayAsync("/api/v1/panel/actualizacion/MonitoreoTI-Panel-Setup-0.2.0.exe")).Should().HaveCount(4);

        foreach (var name in new[] { "secreto.txt", "..%2Fsecreto.txt", "MonitoreoTI-Panel-Setup-0.2.0.exe.config", "MonitoreoTI-Panel-Setup-9.9.9.exe" })
            (await api.Client.GetAsync($"/api/v1/panel/actualizacion/{name}")).StatusCode.Should().Be(HttpStatusCode.NotFound, name);
    }
}
