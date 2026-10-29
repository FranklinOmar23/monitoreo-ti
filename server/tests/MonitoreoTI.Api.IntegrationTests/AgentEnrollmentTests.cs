using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AgentEnrollmentTests(TestDatabase database) : IAsyncLifetime
{
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
    public async Task Valid_token_registers_the_machine_and_stores_only_the_hash()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");
        var mac = AgentTestApi.NewMac();

        var response = await _api.RegisterAsync(token, mac, hostname: "CAJA-99");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var credential = body.GetProperty("credential").GetString()!;
        credential.Should().MatchRegex(@"^\d+\.[A-Za-z0-9_-]{40,}$");

        var machine = await _api.QueryMachineAsync(mac);
        machine.Name.Should().Be("CAJA-99");
        machine.BranchName.Should().Be("Santiago");
        machine.Status.Should().Be("desconectado");
        machine.AgentHash.Should().Be(AgentTokens.Hash(credential)).And.NotContain(credential);
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'agente.registro' AND detalle LIKE '%' + @p + '%'", ("@p", mac)))
            .Should().Be(1);
    }

    [DatabaseFact]
    public async Task Each_registration_consumes_one_use()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");

        (await _api.RegisterAsync(token, AgentTestApi.NewMac())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.RegisterAsync(token, AgentTestApi.NewMac())).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.ScalarAsync("SELECT usos FROM dbo.tokens_registro WHERE token_hash = @p", ("@p", AgentTokens.Hash(token))))
            .Should().Be(2);
    }

    [DatabaseFact]
    public async Task Expired_exhausted_unknown_and_malformed_tokens_are_rejected_alike()
    {
        var company = "Grupo Comercial Caribe";
        var companyId = await database.GetCompanyIdAsync(company);
        var expired = await _api.CreateTokenAsync(company, "Santiago", validFor: TimeSpan.FromMinutes(-5));
        var exhausted = await _api.CreateTokenAsync(company, "Santiago", maxUses: 1, uses: 1);
        var unknown = AgentTokens.Generate(companyId).Token;

        foreach (var token in new[] { expired, exhausted, unknown, "basura" })
        {
            var response = await _api.RegisterAsync(token, AgentTestApi.NewMac());
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, token);
        }

        (await _api.ScalarAsync("SELECT usos FROM dbo.tokens_registro WHERE token_hash = @p", ("@p", AgentTokens.Hash(exhausted))))
            .Should().Be(1, "un token agotado no debe seguir consumiendo usos");
    }

    [DatabaseFact]
    public async Task A_token_secret_cannot_be_reused_with_another_company_prefix()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");
        var rafaelId = await database.GetCompanyIdAsync("Clínica San Rafael");
        var forged = $"{rafaelId}.{token[(token.IndexOf('.') + 1)..]}";
        var mac = AgentTestApi.NewMac();

        var response = await _api.RegisterAsync(forged, mac);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.maquinas WHERE mac = @p", ("@p", mac))).Should().Be(0);
    }

    [DatabaseFact]
    public async Task A_token_registers_machines_only_in_its_own_company()
    {
        var token = await _api.CreateTokenAsync("Clínica San Rafael", "Santo Domingo");
        var mac = AgentTestApi.NewMac();

        (await _api.RegisterAsync(token, mac)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.QueryMachineAsync(mac)).CompanyName.Should().Be("Clínica San Rafael");
    }

    [DatabaseFact]
    public async Task Concurrent_registrations_cannot_exceed_the_use_limit()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago", maxUses: 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _api.RegisterAsync(token, AgentTestApi.NewMac())));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(7);
        (await _api.ScalarAsync("SELECT usos FROM dbo.tokens_registro WHERE token_hash = @p", ("@p", AgentTokens.Hash(token))))
            .Should().Be(1);
    }

    [DatabaseFact]
    public async Task Reinstalling_reuses_the_machine_and_rotates_the_credential()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");
        var mac = AgentTestApi.NewMac();

        var first = await (await _api.RegisterAsync(token, mac)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await _api.RegisterAsync(token, mac, hostname: "RENOMBRADO")).Content.ReadFromJsonAsync<JsonElement>();

        second.GetProperty("machineId").GetInt32().Should().Be(first.GetProperty("machineId").GetInt32());
        second.GetProperty("agentId").GetInt32().Should().Be(first.GetProperty("agentId").GetInt32());
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.maquinas WHERE mac = @p", ("@p", mac))).Should().Be(1);

        var machine = await _api.QueryMachineAsync(mac);
        machine.Name.Should().Be("RENOMBRADO");
        machine.AgentHash.Should().Be(AgentTokens.Hash(second.GetProperty("credential").GetString()!))
            .And.NotBe(AgentTokens.Hash(first.GetProperty("credential").GetString()!));
    }

    [DatabaseFact]
    public async Task Reinstalling_with_a_secondary_card_as_primary_still_finds_the_machine()
    {
        var agent = await _api.RegisterNewAgentAsync();
        var wifiMac = AgentTestApi.NewMac();
        await _api.ExecuteAsync("""
            INSERT INTO dbo.interfaces_red (empresa_id, maquina_id, nombre, mac, tipo)
            SELECT empresa_id, id, N'Wi-Fi', @mac, 'wifi' FROM dbo.maquinas WHERE id = @id
            """, ("@mac", wifiMac), ("@id", agent.MachineId));
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");

        var response = await _api.RegisterAsync(token, wifiMac);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("machineId").GetInt32().Should().Be(agent.MachineId);
        (await _api.ScalarAsync("SELECT mac FROM dbo.maquinas WHERE id = @id", ("@id", agent.MachineId))).Should().Be(wifiMac);
    }

    [DatabaseFact]
    public async Task Invalid_body_is_a_validation_error_and_does_not_consume_the_token()
    {
        var token = await _api.CreateTokenAsync("Grupo Comercial Caribe", "Santiago");

        var response = await _api.RegisterAsync(token, "no-es-una-mac");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _api.ScalarAsync("SELECT usos FROM dbo.tokens_registro WHERE token_hash = @p", ("@p", AgentTokens.Hash(token))))
            .Should().Be(0);
    }
}
