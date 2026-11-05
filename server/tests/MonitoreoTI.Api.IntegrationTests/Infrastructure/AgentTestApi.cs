using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Infrastructure.Auth;

namespace MonitoreoTI.Api.IntegrationTests.Infrastructure;

/// <summary>
/// API en memoria contra la base de pruebas, con ayudas para registrar agentes.
/// Al terminar borra lo que las pruebas crearon: la base se comparte con pruebas que cuentan las filas del seed.
/// Las MAC de prueba empiezan por 02 (administradas localmente); las del seed, por 3C.
/// </summary>
public sealed class AgentTestApi : IAsyncDisposable
{
    private readonly TestDatabase _database;
    private readonly List<string> _tokenHashes = [];

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }

    public AgentTestApi(TestDatabase database, Dictionary<string, string>? settings = null)
    {
        _database = database;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestHost.Configure(builder, database.ConnectionString);
            foreach (var (key, value) in settings ?? [])
                builder.UseSetting(key, value);
        });
        Client = Factory.CreateClient();
    }

    public static string NewMac()
    {
        var bytes = new byte[5];
        Random.Shared.NextBytes(bytes);
        return "02:" + string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    public Task<HttpResponseMessage> RegisterAsync(string token, string mac, string hostname = "PC-PRUEBA") =>
        Client.PostAsJsonAsync("/agent/register", new
        {
            enrollToken = token,
            hostname,
            macAddress = mac,
            ipAddress = "10.0.0.9",
            operatingSystem = "Windows 11 Pro",
            machineType = "computadora",
            agentVersion = "0.1.0",
        });

    /// <summary>Registra un equipo nuevo y devuelve su credencial y sus ids.</summary>
    public async Task<RegisteredAgent> RegisterNewAgentAsync(string company = "Grupo Comercial Caribe", string branch = "Santiago")
    {
        var token = await CreateTokenAsync(company, branch);
        var mac = NewMac();
        var response = await RegisterAsync(token, mac);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new RegisteredAgent(
            mac,
            body.GetProperty("credential").GetString()!,
            body.GetProperty("machineId").GetInt32(),
            body.GetProperty("agentId").GetInt32());
    }

    /// <summary>Crea un token de registro directamente en la base (como lo haría el panel en la Fase 2).</summary>
    public async Task<string> CreateTokenAsync(string company, string branch, TimeSpan? validFor = null, int? maxUses = null, int uses = 0)
    {
        var companyId = await _database.GetCompanyIdAsync(company);
        var (token, hash) = AgentTokens.Generate(companyId);
        _tokenHashes.Add(hash);

        await using var connection = await _database.OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.tokens_registro (empresa_id, sucursal_id, token_hash, vence, usos_max, usos, creado_por)
            SELECT @empresa, s.id, @hash, @vence, @max, @usos, ue.usuario_id
            FROM dbo.sucursales s
            CROSS APPLY (SELECT TOP 1 usuario_id FROM dbo.usuario_empresa WHERE empresa_id = @empresa) ue
            WHERE s.empresa_id = @empresa AND s.nombre = @sucursal
            """, connection);
        command.Parameters.AddWithValue("@empresa", companyId);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@vence", DateTime.UtcNow + (validFor ?? TimeSpan.FromDays(1)));
        command.Parameters.AddWithValue("@max", (object?)maxUses ?? DBNull.Value);
        command.Parameters.AddWithValue("@usos", uses);
        command.Parameters.AddWithValue("@sucursal", branch);
        (await command.ExecuteNonQueryAsync()).Should().Be(1, "la sucursal de prueba debe existir");
        return token;
    }

    public async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await _database.OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await _database.OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<MachineRow> QueryMachineAsync(string mac)
    {
        await using var connection = await _database.OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand("""
            SELECT m.nombre, m.estado, s.nombre, e.nombre, a.token_hash, a.version, a.ultimo_contacto
            FROM dbo.maquinas m
            JOIN dbo.sucursales s ON s.empresa_id = m.empresa_id AND s.id = m.sucursal_id
            JOIN dbo.empresas e ON e.id = m.empresa_id
            JOIN dbo.agentes a ON a.empresa_id = m.empresa_id AND a.maquina_id = m.id
            WHERE m.mac = @mac
            """, connection);
        command.Parameters.AddWithValue("@mac", mac);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"debe existir la máquina con MAC {mac}");
        return new MachineRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6));
    }

    /// <summary>
    /// Crea un usuario de prueba con ese rol en la empresa (y, si se indican, sus sucursales asignadas) y
    /// devuelve un token de acceso como el que emitiría el login. Evita repetir el 2FA en cada prueba del panel.
    /// </summary>
    public async Task<string> TokenForAsync(string company, string role, params string[] branches)
    {
        var email = $"u{Guid.NewGuid():N}@prueba.test";
        var companyId = await _database.GetCompanyIdAsync(company);

        await ExecuteAsync("INSERT INTO dbo.usuarios (nombre, correo, password_hash) VALUES (N'Usuario de prueba', @c, N'x')", ("@c", email));
        await ExecuteAsync("""
            INSERT INTO dbo.usuario_empresa (usuario_id, empresa_id, rol_id)
            SELECT u.id, @e, r.id FROM dbo.usuarios u, dbo.roles r WHERE u.correo = @c AND r.nombre = @r AND r.empresa_id IS NULL
            """, ("@c", email), ("@e", companyId), ("@r", role));
        foreach (var branch in branches)
        {
            await ExecuteAsync("""
                INSERT INTO dbo.usuario_sucursal (empresa_id, usuario_id, sucursal_id)
                SELECT @e, u.id, s.id FROM dbo.usuarios u, dbo.sucursales s WHERE u.correo = @c AND s.empresa_id = @e AND s.nombre = @s
                """, ("@c", email), ("@e", companyId), ("@s", branch));
        }

        var userId = (int)(await ScalarAsync("SELECT id FROM dbo.usuarios WHERE correo = @c", ("@c", email)))!;
        var permissions = new List<string>();
        await using (var connection = await _database.OpenConnectionAsync(superAdmin: true))
        await using (var command = new SqlCommand("""
            SELECT p.clave FROM dbo.permisos p JOIN dbo.rol_permiso rp ON rp.permiso_id = p.id
            JOIN dbo.roles r ON r.id = rp.rol_id WHERE r.nombre = @r AND r.empresa_id IS NULL
            """, connection))
        {
            command.Parameters.AddWithValue("@r", role);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                permissions.Add(reader.GetString(0));
        }

        var tokens = Factory.Services.GetRequiredService<AuthTokenService>();
        return tokens.CreateAccessToken(new AccessTokenContent(userId, "Usuario de prueba", email, companyId, role, false, permissions)).Token;
    }

    /// <summary>Cliente HTTP con la sesión de ese token.</summary>
    public HttpClient ClientWith(string accessToken)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        await using var connection = await _database.OpenConnectionAsync(superAdmin: true);
        await TestDatabase.ExecuteAsync(connection, """
            DELETE FROM dbo.auditoria WHERE accion LIKE 'agente.%' AND detalle LIKE '%"mac":"02:%';
            DELETE a FROM dbo.auditoria a JOIN dbo.maquinas m ON a.entidad = 'maquinas' AND a.entidad_id = CAST(m.id AS VARCHAR(20)) WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            DELETE FROM dbo.reglas_alerta WHERE sucursal_id IS NOT NULL;
            DELETE FROM dbo.tokens_registro WHERE nombre = N'Volver a ver';
            DELETE FROM dbo.subredes WHERE nombre LIKE N'Red 10.%'
                OR recolector_id IN (SELECT a.id FROM dbo.agentes a JOIN dbo.maquinas m ON m.id = a.maquina_id WHERE m.mac LIKE '02:%')
                OR dispositivo_arp_id IN (SELECT id FROM dbo.dispositivos_red WHERE nombre LIKE 'PRUEBA-%');
            DELETE c FROM dbo.cableado c JOIN dbo.puertos p ON p.id IN (c.puerto_a_id, c.puerto_b_id)
                JOIN dbo.dispositivos_red d ON d.id = p.dispositivo_id WHERE d.nombre LIKE 'PRUEBA-%';
            DELETE c FROM dbo.cableado c JOIN dbo.puntos_red r ON r.id = c.punto_red_id WHERE r.codigo LIKE 'PRUEBA-%';
            DELETE al FROM dbo.alertas al JOIN dbo.dispositivos_red d ON d.empresa_id = al.empresa_id AND d.id = al.dispositivo_id WHERE d.nombre LIKE 'PRUEBA-%';
            DELETE l FROM dbo.enlaces_red l JOIN dbo.puertos p ON p.id IN (l.puerto_origen_id, l.puerto_destino_id)
                JOIN dbo.dispositivos_red d ON d.id = p.dispositivo_id WHERE d.nombre LIKE 'PRUEBA-%';
            DELETE a FROM dbo.auditoria a JOIN dbo.dispositivos_red d ON a.entidad = 'dispositivos_red' AND a.entidad_id = CAST(d.id AS VARCHAR(20))
                WHERE d.nombre LIKE 'PRUEBA-%' AND a.usuario_id IS NULL;
            DELETE a FROM dbo.auditoria a JOIN dbo.puertos p ON a.entidad = 'puertos' AND a.entidad_id = CAST(p.id AS VARCHAR(20))
                JOIN dbo.dispositivos_red d ON d.id = p.dispositivo_id WHERE d.nombre LIKE 'PRUEBA-%' AND a.usuario_id IS NULL;
            DELETE FROM dbo.dispositivos_red WHERE nombre LIKE 'PRUEBA-%';
            DELETE FROM dbo.puntos_red WHERE codigo LIKE 'PRUEBA-%';
            DELETE FROM dbo.gabinetes WHERE nombre LIKE 'PRUEBA-%';
            DELETE FROM dbo.salas WHERE nombre LIKE 'Prueba %';
            DELETE FROM dbo.modelos_dispositivo WHERE marca = N'Prueba';
            DELETE a FROM dbo.auditoria a JOIN dbo.tickets t ON a.entidad = 'tickets' AND a.entidad_id = CAST(t.id AS VARCHAR(20))
                JOIN dbo.usuarios u ON u.id = t.creado_por WHERE u.correo LIKE '%@prueba.test';
            DELETE t FROM dbo.tickets t JOIN dbo.usuarios u ON u.id = t.creado_por WHERE u.correo LIKE '%@prueba.test';
            DELETE t FROM dbo.tickets t JOIN dbo.maquinas m ON m.empresa_id = t.empresa_id AND m.id = t.maquina_id WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            DELETE c FROM dbo.ticket_comentarios c JOIN dbo.usuarios u ON u.id = c.usuario_id WHERE u.correo LIKE '%@prueba.test';
            UPDATE t SET asignado_a = NULL FROM dbo.tickets t JOIN dbo.usuarios u ON u.id = t.asignado_a WHERE u.correo LIKE '%@prueba.test';
            UPDATE a SET atendida_por = NULL FROM dbo.alertas a JOIN dbo.usuarios u ON u.id = a.atendida_por WHERE u.correo LIKE '%@prueba.test';
            DELETE al FROM dbo.alertas al JOIN dbo.maquinas m ON m.empresa_id = al.empresa_id AND m.id = al.maquina_id WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            DELETE x FROM dbo.mantenimientos x JOIN dbo.maquinas m ON m.empresa_id = x.empresa_id AND m.id = x.maquina_id WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            UPDATE dbo.maquinas SET recolector_id = NULL WHERE recolector_id IS NOT NULL AND (mac LIKE '02:%' OR nombre LIKE 'PRUEBA-%'
                OR recolector_id IN (SELECT a.id FROM dbo.agentes a JOIN dbo.maquinas m ON m.id = a.maquina_id WHERE m.mac LIKE '02:%'));
            DELETE a FROM dbo.auditoria a JOIN dbo.comandos c ON a.entidad = 'comandos' AND a.entidad_id = CAST(c.id AS VARCHAR(20))
                JOIN dbo.maquinas m ON m.empresa_id = c.empresa_id AND m.id = c.maquina_id WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            DELETE c FROM dbo.comandos c JOIN dbo.maquinas m ON m.empresa_id = c.empresa_id AND m.id = c.maquina_id WHERE m.mac LIKE '02:%' OR m.nombre LIKE 'PRUEBA-%';
            DELETE c FROM dbo.comandos c JOIN dbo.usuarios u ON u.id = c.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE s FROM dbo.sesiones_remotas s JOIN dbo.usuarios u ON u.id = s.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE s FROM dbo.scripts s JOIN dbo.usuarios u ON u.id = s.creado_por WHERE u.correo LIKE '%@prueba.test';
            DELETE FROM dbo.maquinas WHERE mac LIKE '02:%' OR nombre LIKE 'PRUEBA-%';
            DELETE a FROM dbo.auditoria a JOIN dbo.usuarios u ON u.id = a.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE us FROM dbo.usuario_sucursal us JOIN dbo.usuarios u ON u.id = us.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE t FROM dbo.tokens_registro t JOIN dbo.usuarios u ON u.id = t.creado_por WHERE u.correo LIKE '%@prueba.test';
            DELETE FROM dbo.sucursales WHERE nombre LIKE 'Prueba %';
            DELETE ue FROM dbo.usuario_empresa ue JOIN dbo.usuarios u ON u.id = ue.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE FROM dbo.usuarios WHERE correo LIKE '%@prueba.test';
            DELETE FROM dbo.roles WHERE nombre LIKE 'Prueba %' AND empresa_id IS NOT NULL;
            """);
        foreach (var hash in _tokenHashes)
            await TestDatabase.ExecuteAsync(connection, $"DELETE FROM dbo.tokens_registro WHERE token_hash = '{hash}';");
    }
}

public sealed record RegisteredAgent(string Mac, string Credential, int MachineId, int AgentId);

public sealed record MachineRow(
    string Name, string Status, string BranchName, string CompanyName, string AgentHash, string AgentVersion, DateTime? LastContact);
