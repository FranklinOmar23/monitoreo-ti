using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Infrastructure.Auth;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Usuarios (W-36, W-37), roles y permisos (W-38) y Mi perfil (W-41).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class UserAdministrationTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private const string Password = "Prueba.Segura1";
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

    private async Task<HttpClient> AdminAsync() => _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));

    private static string NewEmail() => $"u{Guid.NewGuid():N}@prueba.test";

    // ---------------------------------------------------------------- usuarios

    [DatabaseFact]
    public async Task A_new_user_gets_a_one_time_link_to_create_the_password_and_then_logs_in()
    {
        var admin = await AdminAsync();
        var email = NewEmail();
        var santiago = (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = N'Santiago'", ("@e", Company)))!;

        var created = await admin.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "Rosa Nueva", correo = email, rolId = await RoleIdAsync("Empleado"), sucursalIds = new[] { santiago } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadAsync(created);
        var id = body.GetProperty("id").GetInt32();

        // En pruebas el correo se guarda en archivo (no llega a un buzón): el enlace vuelve al panel para entregarlo a mano.
        var link = body.GetProperty("enlace");
        link.GetProperty("emailed").GetBoolean().Should().BeFalse();
        var token = link.GetProperty("link").GetString()!.Split("token=")[1];
        File.ReadAllText(Directory.GetFiles(TestHost.EmailFolder).Single(f => File.ReadAllText(f).Contains(email))).Should().Contain(token);

        var list = (await (await admin.GetAsync("/api/v1/usuarios?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        var row = list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        row.GetProperty("roleName").GetString().Should().Be("Empleado");
        row.GetProperty("pending").GetBoolean().Should().BeTrue();
        row.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("name").GetString()).Should().Equal("Santiago");

        // La auditoría registra la invitación, nunca el token.
        var audit = (string?)await _api.ScalarAsync("SELECT TOP 1 detalle FROM dbo.auditoria WHERE accion = 'usuario.invitacion' AND entidad_id = @id ORDER BY id DESC",
            ("@id", id.ToString()));
        audit.Should().NotContain(token);

        (await _api.Client.PostAsJsonAsync("/api/v1/auth/restablecer", new { token, nuevaContrasena = "MiClave2026x" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/login", new { correo = email, contrasena = "MiClave2026x" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "Otra", correo = email, rolId = await RoleIdAsync("Empleado") }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DatabaseFact]
    public async Task A_person_from_another_company_joins_with_their_own_password_and_only_role_and_branches_change_here()
    {
        var email = await CreateUserAsync("Clínica San Rafael", "Tecnico");
        var admin = await AdminAsync();

        var created = await admin.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "Otro nombre", correo = email, rolId = await RoleIdAsync("Supervisor") });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadAsync(created);
        body.GetProperty("enlace").ValueKind.Should().Be(JsonValueKind.Null);
        var id = body.GetProperty("id").GetInt32();

        (await admin.PutAsJsonAsync($"/api/v1/usuarios/{id}", new { nombre = "Cambiado", correo = email, rolId = await RoleIdAsync("Supervisor") }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"/api/v1/usuarios/{id}/enlace-contrasena", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/usuarios/{id}", new { nombre = "Usuario de prueba", correo = email, rolId = await RoleIdAsync("Empleado") }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.ScalarAsync("SELECT nombre FROM dbo.usuarios WHERE id = @id", ("@id", id))).Should().Be("Usuario de prueba");
    }

    [DatabaseFact]
    public async Task Deactivating_ends_the_session_in_this_company_and_nobody_deactivates_or_demotes_themselves()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var admin = _api.ClientWith(token);
        var selfId = int.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).Subject);

        (await admin.PostAsync($"/api/v1/usuarios/{selfId}/desactivar", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync($"/api/v1/usuarios/{selfId}", new { nombre = "Usuario de prueba", correo = SelfEmail(token), rolId = await RoleIdAsync("Tecnico") }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var email = await CreateUserAsync(Company, "Empleado");
        var refresh = (await ReadAsync(await LoginAsync(email))).GetProperty("sesion").GetProperty("refreshToken").GetString()!;
        var id = await UserIdAsync(email);

        (await admin.PostAsync($"/api/v1/usuarios/{id}/desactivar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(email)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await admin.PostAsync($"/api/v1/usuarios/{id}/activar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoginAsync(email)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DatabaseFact]
    public async Task Resetting_two_factor_clears_it_and_closes_every_session()
    {
        var email = await CreateUserAsync(Company, "Empleado");
        var refresh = (await ReadAsync(await LoginAsync(email))).GetProperty("sesion").GetProperty("refreshToken").GetString()!;
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET totp_secreto = N'cifrado' WHERE correo = @c", ("@c", email));
        var admin = await AdminAsync();
        var id = await UserIdAsync(email);

        (await admin.PostAsync($"/api/v1/usuarios/{id}/reiniciar-2fa", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.ScalarAsync("SELECT totp_secreto FROM dbo.usuarios WHERE id = @id", ("@id", id))).Should().Be(DBNull.Value);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.PostAsync($"/api/v1/usuarios/{id}/reiniciar-2fa", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task Only_user_administrators_manage_users_and_roles()
    {
        var tech = _api.ClientWith(await _api.TokenForAsync(Company, "Tecnico"));

        (await tech.GetAsync("/api/v1/usuarios")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tech.GetAsync("/api/v1/roles")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await tech.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "X", correo = NewEmail(), rolId = 1 })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [DatabaseFact]
    public async Task A_branch_limited_administrator_only_grants_their_own_branches()
    {
        // Un rol propio con usuarios.administrar, para alguien que solo ve Santiago.
        var full = await AdminAsync();
        var roleId = await CreateRoleAsync(full, "Prueba Jefe local", "usuarios.administrar", "equipos.ver");
        var email = await CreateUserAsync(Company, "Empleado");
        var limitedId = await UserIdAsync(email);
        var santiago = await BranchIdAsync("Santiago");
        var santoDomingo = await BranchIdAsync("Santo Domingo");
        (await full.PutAsJsonAsync($"/api/v1/usuarios/{limitedId}", new { nombre = "Usuario de prueba", correo = email, rolId = roleId, sucursalIds = new[] { santiago } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var limited = _api.ClientWith(await TokenWithRoleAsync(limitedId, email, "Prueba Jefe local", "usuarios.administrar"));
        var empleado = await RoleIdAsync("Empleado");

        (await limited.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "A", correo = NewEmail(), rolId = empleado })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await limited.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "A", correo = NewEmail(), rolId = empleado, sucursalIds = new[] { santoDomingo } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await limited.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "A", correo = NewEmail(), rolId = empleado, sucursalIds = new[] { santiago } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- roles

    [DatabaseFact]
    public async Task System_roles_are_read_only_and_company_roles_are_created_used_and_deleted_when_free()
    {
        var admin = await AdminAsync();

        var roles = await (await admin.GetAsync("/api/v1/roles")).Content.ReadFromJsonAsync<JsonElement>();
        var administrador = roles.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "Administrador");
        administrador.GetProperty("isSystem").GetBoolean().Should().BeTrue();
        (await admin.PutAsJsonAsync($"/api/v1/roles/{administrador.GetProperty("id").GetInt32()}", new { nombre = "Administrador", permisos = new[] { "equipos.ver" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync("/api/v1/roles", new { nombre = "Tecnico", permisos = Array.Empty<string>() })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/roles", new { nombre = "Prueba Rara", permisos = new[] { "no.existe" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var roleId = await CreateRoleAsync(admin, "Prueba Mesa de ayuda", "tickets.crear", "tickets.gestionar", "equipos.ver");
        (await admin.PutAsJsonAsync($"/api/v1/roles/{roleId}", new { nombre = "Prueba Mesa de ayuda", descripcion = "Primer nivel", permisos = new[] { "tickets.gestionar", "remoto.conectar" } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var permisos = await (await admin.GetAsync("/api/v1/permisos")).Content.ReadFromJsonAsync<JsonElement>();
        permisos.EnumerateArray().Single(p => p.GetProperty("key").GetString() == "remoto.conectar").GetProperty("requiresTwoFactor").GetBoolean().Should().BeTrue();

        var email = NewEmail();
        var user = await ReadAsync(await admin.PostAsJsonAsync("/api/v1/usuarios", new { nombre = "Con rol propio", correo = email, rolId = roleId }));
        var list = (await (await admin.GetAsync("/api/v1/usuarios?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == user.GetProperty("id").GetInt32())
            .GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue("el rol da acceso remoto");

        (await admin.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/usuarios/{user.GetProperty("id").GetInt32()}", new { nombre = "Con rol propio", correo = email, rolId = await RoleIdAsync("Empleado") }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/v1/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- mi perfil

    [DatabaseFact]
    public async Task Changing_the_password_requires_the_current_one_and_closes_only_the_other_sessions()
    {
        var email = await CreateUserAsync(Company, "Empleado");
        var other = (await ReadAsync(await LoginAsync(email, "Equipo/PC-CASA"))).GetProperty("sesion");
        var current = (await ReadAsync(await LoginAsync(email, "Mozilla/5.0 (Windows NT 10.0) Electron/33 Equipo/PC-OFICINA"))).GetProperty("sesion");
        var me = _api.ClientWith(current.GetProperty("accessToken").GetString()!);

        var sessions = (await (await me.GetAsync("/api/v1/auth/sesiones?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        sessions.GetArrayLength().Should().Be(2);
        var mine = sessions.EnumerateArray().Single(s => s.GetProperty("current").GetBoolean());
        mine.GetProperty("device").GetString().Should().Be("Centinela en PC-OFICINA (Windows)");

        (await me.PostAsJsonAsync("/api/v1/auth/contrasena", new { contrasenaActual = "equivocada1", nuevaContrasena = "NuevaClave2026" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await me.PostAsJsonAsync("/api/v1/auth/contrasena", new { contrasenaActual = Password, nuevaContrasena = "corta" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await me.PostAsJsonAsync("/api/v1/auth/contrasena", new { contrasenaActual = Password, nuevaContrasena = "NuevaClave2026" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = other.GetProperty("refreshToken").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = current.GetProperty("refreshToken").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/login", new { correo = email, contrasena = "NuevaClave2026" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DatabaseFact]
    public async Task Users_see_and_close_only_their_own_sessions_and_rename_themselves()
    {
        var email = await CreateUserAsync(Company, "Empleado");
        var first = (await ReadAsync(await LoginAsync(email))).GetProperty("sesion");
        var second = (await ReadAsync(await LoginAsync(email))).GetProperty("sesion");
        var me = _api.ClientWith(second.GetProperty("accessToken").GetString()!);
        var stranger = _api.ClientWith(await _api.TokenForAsync(Company, "Empleado"));

        var sessions = (await (await me.GetAsync("/api/v1/auth/sesiones?limit=100")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("data");
        var otherId = sessions.EnumerateArray().Single(s => !s.GetProperty("current").GetBoolean()).GetProperty("id").GetInt64();

        (await stranger.DeleteAsync($"/api/v1/auth/sesiones/{otherId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await me.DeleteAsync($"/api/v1/auth/sesiones/{otherId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.Client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = first.GetProperty("refreshToken").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await me.PutAsJsonAsync("/api/v1/auth/perfil", new { nombre = "  Nombre Nuevo " })).StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await (await me.GetAsync("/api/v1/auth/perfil")).Content.ReadFromJsonAsync<JsonElement>();
        profile.GetProperty("name").GetString().Should().Be("Nombre Nuevo");
        profile.GetProperty("email").GetString().Should().Be(email);
    }

    // ---------------------------------------------------------------- ayudas

    private async Task<string> CreateUserAsync(string company, string role)
    {
        var email = NewEmail();
        await _api.ExecuteAsync("INSERT INTO dbo.usuarios (nombre, correo, password_hash) VALUES (N'Usuario de prueba', @c, @h)",
            ("@c", email), ("@h", BCrypt.Net.BCrypt.HashPassword(Password, 4)));
        await _api.ExecuteAsync("""
            INSERT INTO dbo.usuario_empresa (usuario_id, empresa_id, rol_id)
            SELECT u.id, e.id, r.id FROM dbo.usuarios u, dbo.empresas e, dbo.roles r
            WHERE u.correo = @c AND e.nombre = @e AND r.nombre = @r AND r.empresa_id IS NULL
            """, ("@c", email), ("@e", company), ("@r", role));
        return email;
    }

    private Task<HttpResponseMessage> LoginAsync(string email, string? userAgent = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { correo = email, contrasena = Password }),
        };
        if (userAgent is not null)
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return _api.Client.SendAsync(request);
    }

    private async Task<int> RoleIdAsync(string name) =>
        (int)(await _api.ScalarAsync("SELECT id FROM dbo.roles WHERE nombre = @r AND empresa_id IS NULL", ("@r", name)))!;

    private async Task<int> UserIdAsync(string email) =>
        (int)(await _api.ScalarAsync("SELECT id FROM dbo.usuarios WHERE correo = @c", ("@c", email)))!;

    private async Task<int> BranchIdAsync(string name) =>
        (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = @s", ("@e", Company), ("@s", name)))!;

    private static async Task<int> CreateRoleAsync(HttpClient admin, string name, params string[] permissions)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/roles", new { nombre = name, permisos = permissions });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadAsync(response)).GetProperty("id").GetInt32();
    }

    private async Task<string> TokenWithRoleAsync(int userId, string email, string role, params string[] permissions)
    {
        var companyId = await database.GetCompanyIdAsync(Company);
        var tokens = (AuthTokenService)_api.Factory.Services.GetService(typeof(AuthTokenService))!;
        return tokens.CreateAccessToken(new AccessTokenContent(userId, "Usuario de prueba", email, companyId, role, false, permissions)).Token;
    }

    private static string SelfEmail(string token) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
