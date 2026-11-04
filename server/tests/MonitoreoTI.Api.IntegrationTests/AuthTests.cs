using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.IdentityModel.JsonWebTokens;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using OtpNet;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AuthTests(TestDatabase database) : IAsyncLifetime
{
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
        if (_api is null)
            return;

        // Usuarios creados por estas pruebas (dominio prueba.test); el seed queda intacto.
        await _api.ExecuteAsync("""
            DELETE a FROM dbo.auditoria a JOIN dbo.usuarios u ON u.id = a.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE ue FROM dbo.usuario_empresa ue JOIN dbo.usuarios u ON u.id = ue.usuario_id WHERE u.correo LIKE '%@prueba.test';
            DELETE FROM dbo.usuarios WHERE correo LIKE '%@prueba.test';
            """);
        await _api.DisposeAsync();
    }

    private HttpClient Client => _api.Client;

    // ---------------------------------------------------------------- contraseña y bloqueo

    [DatabaseFact]
    public async Task Wrong_password_and_unknown_email_get_the_same_answer()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));

        var wrong = await PostAsync("login", new { correo = email, contrasena = "otra-cosa-123" });
        var unknown = await PostAsync("login", new { correo = "nadie@prueba.test", contrasena = Password });

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(wrong)).Should().Be("credenciales_invalidas");
        (await Code(unknown)).Should().Be("credenciales_invalidas");
    }

    [DatabaseFact]
    public async Task Five_failures_lock_the_account_even_for_the_right_password()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));

        for (var i = 0; i < 4; i++)
            (await PostAsync("login", new { correo = email, contrasena = "mala-clave-1" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var fifth = await PostAsync("login", new { correo = email, contrasena = "mala-clave-1" });
        fifth.StatusCode.Should().Be((HttpStatusCode)423);

        var correct = await PostAsync("login", new { correo = email, contrasena = Password });
        correct.StatusCode.Should().Be((HttpStatusCode)423);
        (await Code(correct)).Should().Be("cuenta_bloqueada");
    }

    [DatabaseFact]
    public async Task Inactive_users_cannot_log_in()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET activo = 0 WHERE correo = @c", ("@c", email));

        (await PostAsync("login", new { correo = email, contrasena = Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- sesión directa y token

    [DatabaseFact]
    public async Task A_user_without_remote_permissions_and_one_company_gets_a_session_directly()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));

        var body = await LoginAsync(email);

        body.GetProperty("paso").GetString().Should().Be("completado");
        var access = body.GetProperty("sesion").GetProperty("accessToken").GetString()!;
        var token = new JsonWebToken(access);
        token.GetClaim("empresa_id").Value.Should().Be((await database.GetCompanyIdAsync("Grupo Comercial Caribe")).ToString());
        token.Claims.Where(c => c.Type == "perm").Select(c => c.Value).Should().BeEquivalentTo("equipos.ver", "tickets.crear");
        token.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(15), TimeSpan.FromMinutes(1));

        var session = await GetSessionAsync(access);
        session.GetProperty("empresa").GetProperty("nombre").GetString().Should().Be("Grupo Comercial Caribe");
        session.GetProperty("empresa").GetProperty("rol").GetString().Should().Be("Empleado");
    }

    [DatabaseFact]
    public async Task Api_calls_without_a_valid_access_token_are_rejected()
    {
        (await Client.GetAsync("/api/v1/auth/sesion")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var forged = new JsonWebTokenHandler().CreateToken("""{"sub":"1","empresa_id":"1"}""");
        (await GetAsync("sesion", forged)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- 2FA

    [DatabaseFact]
    public async Task Remote_access_roles_must_set_up_two_factor_and_then_always_use_it()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Tecnico"));

        var first = await LoginAsync(email);
        first.GetProperty("paso").GetString().Should().Be("activar-2fa");
        first.TryGetProperty("sesion", out var noSession).Should().BeTrue();
        noSession.ValueKind.Should().Be(JsonValueKind.Null, "sin 2FA no hay sesión");
        var setup = first.GetProperty("activacion2fa");
        var secret = setup.GetProperty("secreto").GetString()!;
        setup.GetProperty("uri").GetString().Should().StartWith("otpauth://totp/MonitoreoTI:");
        setup.GetProperty("qr").GetString().Should().StartWith("data:image/png;base64,");

        var wrong = await PostAsync("activar-2fa", new { desafio = Challenge(first), codigo = "000000" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var confirmed = await ReadAsync(await PostAsync("activar-2fa", new { desafio = Challenge(first), codigo = Code(secret) }));
        confirmed.GetProperty("paso").GetString().Should().Be("completado");
        (await _api.ScalarAsync("SELECT totp_secreto FROM dbo.usuarios WHERE correo = @c", ("@c", email)) as string)
            .Should().StartWith("v1:").And.NotContain(secret, "el secreto se guarda cifrado");

        // Siguiente inicio de sesión: pide el código.
        var second = await LoginAsync(email);
        second.GetProperty("paso").GetString().Should().Be("verificar-2fa");
        var nextCode = Code(secret, DateTime.UtcNow.AddSeconds(30));

        var verified = await PostAsync("verificar-2fa", new { desafio = Challenge(second), codigo = nextCode });
        verified.StatusCode.Should().Be(HttpStatusCode.OK);

        // Un código ya usado no sirve otra vez.
        var third = await LoginAsync(email);
        var replay = await PostAsync("verificar-2fa", new { desafio = Challenge(third), codigo = nextCode });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Code(replay)).Should().Be("codigo_invalido");
    }

    [DatabaseFact]
    public async Task Challenges_are_not_access_tokens_and_cannot_skip_steps()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Tecnico"));
        var first = await LoginAsync(email);
        var challenge = Challenge(first);

        (await GetAsync("sesion", challenge)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Un desafío de "activar 2FA" no sirve para elegir empresa ni para verificar.
        (await PostAsync("seleccionar-empresa", new { desafio = challenge, empresaId = 1 })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync("verificar-2fa", new { desafio = challenge, codigo = "123456" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Un token de acceso tampoco sirve como desafío.
        var plain = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var access = (await LoginAsync(plain)).GetProperty("sesion").GetProperty("accessToken").GetString();
        (await PostAsync("seleccionar-empresa", new { desafio = access, empresaId = 1 })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- varias empresas

    [DatabaseFact]
    public async Task Users_in_several_companies_choose_one_and_only_among_their_own()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"), ("Clínica San Rafael", "Supervisor"));
        var rafael = await database.GetCompanyIdAsync("Clínica San Rafael");

        var login = await LoginAsync(email);
        login.GetProperty("paso").GetString().Should().Be("elegir-empresa");
        login.GetProperty("empresas").EnumerateArray().Select(e => e.GetProperty("nombre").GetString())
            .Should().BeEquivalentTo("Clínica San Rafael", "Grupo Comercial Caribe");

        var foreign = await PostAsync("seleccionar-empresa", new { desafio = Challenge(login), empresaId = 999_999 });
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var chosen = await ReadAsync(await PostAsync("seleccionar-empresa", new { desafio = Challenge(login), empresaId = rafael }));
        var access = chosen.GetProperty("sesion").GetProperty("accessToken").GetString()!;
        new JsonWebToken(access).GetClaim("empresa_id").Value.Should().Be(rafael.ToString());

        var session = await GetSessionAsync(access);
        session.GetProperty("empresa").GetProperty("rol").GetString().Should().Be("Supervisor");
        session.GetProperty("empresas").GetArrayLength().Should().Be(2);
    }

    [DatabaseFact]
    public async Task Switching_company_issues_a_session_for_the_other_company_only_if_a_member()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"), ("Clínica San Rafael", "Supervisor"));
        var caribe = await database.GetCompanyIdAsync("Grupo Comercial Caribe");
        var rafael = await database.GetCompanyIdAsync("Clínica San Rafael");
        var login = await LoginAsync(email);
        var access = (await ReadAsync(await PostAsync("seleccionar-empresa", new { desafio = Challenge(login), empresaId = caribe })))
            .GetProperty("sesion").GetProperty("accessToken").GetString()!;

        var switched = await SendAsync(HttpMethod.Post, "cambiar-empresa", access, new { empresaId = rafael });
        switched.StatusCode.Should().Be(HttpStatusCode.OK);
        new JsonWebToken((await ReadAsync(switched)).GetProperty("accessToken").GetString()!).GetClaim("empresa_id").Value
            .Should().Be(rafael.ToString());

        var notMember = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var otherAccess = (await LoginAsync(notMember)).GetProperty("sesion").GetProperty("accessToken").GetString()!;
        (await SendAsync(HttpMethod.Post, "cambiar-empresa", otherAccess, new { empresaId = rafael })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- refresh y logout

    [DatabaseFact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_one_closes_every_session()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var first = (await LoginAsync(email)).GetProperty("sesion").GetProperty("refreshToken").GetString()!;

        var rotated = await PostAsync("refresh", new { refreshToken = first });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await ReadAsync(rotated)).GetProperty("refreshToken").GetString()!;
        second.Should().NotBe(first);

        // Alguien usa el refresh viejo (robado): se rechaza y se cierran todas las sesiones, incluida la nueva.
        (await PostAsync("refresh", new { refreshToken = first })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync("refresh", new { refreshToken = second })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _api.ScalarAsync("""
            SELECT COUNT(*) FROM dbo.auditoria a JOIN dbo.usuarios u ON u.id = a.usuario_id
            WHERE u.correo = @c AND a.accion = 'sesion.reuso_refresh'
            """, ("@c", email))).Should().Be(1, "solo el refresh ya rotado delata la copia; el que cerró la protección solo deja de servir");
    }

    [DatabaseFact]
    public async Task Logout_revokes_the_refresh_token_and_a_role_change_shows_on_refresh()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var refresh = (await LoginAsync(email)).GetProperty("sesion").GetProperty("refreshToken").GetString()!;

        await _api.ExecuteAsync("""
            UPDATE ue SET rol_id = (SELECT id FROM dbo.roles WHERE nombre = N'Supervisor' AND empresa_id IS NULL)
            FROM dbo.usuario_empresa ue JOIN dbo.usuarios u ON u.id = ue.usuario_id WHERE u.correo = @c
            """, ("@c", email));
        var refreshed = await ReadAsync(await PostAsync("refresh", new { refreshToken = refresh }));
        new JsonWebToken(refreshed.GetProperty("accessToken").GetString()!).GetClaim("rol").Value.Should().Be("Supervisor");

        var latest = refreshed.GetProperty("refreshToken").GetString()!;
        (await PostAsync("logout", new { refreshToken = latest })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PostAsync("refresh", new { refreshToken = latest })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task Removing_a_user_from_the_company_ends_access_at_the_next_refresh()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var refresh = (await LoginAsync(email)).GetProperty("sesion").GetProperty("refreshToken").GetString()!;

        await _api.ExecuteAsync("""
            UPDATE ue SET activo = 0 FROM dbo.usuario_empresa ue JOIN dbo.usuarios u ON u.id = ue.usuario_id WHERE u.correo = @c
            """, ("@c", email));

        (await PostAsync("refresh", new { refreshToken = refresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------- recuperar contraseña

    [DatabaseFact]
    public async Task Password_reset_link_works_once_and_closes_open_sessions()
    {
        var email = await CreateUserAsync(("Grupo Comercial Caribe", "Empleado"));
        var oldRefresh = (await LoginAsync(email)).GetProperty("sesion").GetProperty("refreshToken").GetString()!;

        (await PostAsync("recuperar", new { correo = email })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var token = await ReadResetTokenAsync(email);

        (await PostAsync("restablecer", new { token, nuevaContrasena = "corta1" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PostAsync("restablecer", new { token, nuevaContrasena = "NuevaClave2026" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PostAsync("restablecer", new { token, nuevaContrasena = "OtraClave2026" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await PostAsync("login", new { correo = email, contrasena = Password })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await PostAsync("login", new { correo = email, contrasena = "NuevaClave2026" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync("refresh", new { refreshToken = oldRefresh })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task Password_reset_does_not_reveal_whether_an_email_exists()
    {
        var before = Directory.Exists(TestHost.EmailFolder) ? Directory.GetFiles(TestHost.EmailFolder).Length : 0;

        var response = await PostAsync("recuperar", new { correo = "no-existe@prueba.test" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (Directory.Exists(TestHost.EmailFolder) ? Directory.GetFiles(TestHost.EmailFolder).Length : 0).Should().Be(before);
    }

    // ---------------------------------------------------------------- ayudas

    private async Task<string> CreateUserAsync(params (string Company, string Role)[] memberships)
    {
        var email = $"u{Guid.NewGuid():N}@prueba.test";
        await _api.ExecuteAsync(
            "INSERT INTO dbo.usuarios (nombre, correo, password_hash) VALUES (N'Usuario de prueba', @c, @h)",
            ("@c", email), ("@h", BCrypt.Net.BCrypt.HashPassword(Password, 4)));

        foreach (var (company, role) in memberships)
        {
            await _api.ExecuteAsync("""
                INSERT INTO dbo.usuario_empresa (usuario_id, empresa_id, rol_id)
                SELECT u.id, e.id, r.id FROM dbo.usuarios u, dbo.empresas e, dbo.roles r
                WHERE u.correo = @c AND e.nombre = @e AND r.nombre = @r AND r.empresa_id IS NULL
                """, ("@c", email), ("@e", company), ("@r", role));
        }
        return email;
    }

    private async Task<JsonElement> LoginAsync(string email)
    {
        var response = await PostAsync("login", new { correo = email, contrasena = Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync(response);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body) => Client.PostAsJsonAsync($"/api/v1/auth/{path}", body);

    private Task<HttpResponseMessage> GetAsync(string path, string accessToken) => SendAsync(HttpMethod.Get, path, accessToken);

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"/api/v1/auth/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    private async Task<JsonElement> GetSessionAsync(string accessToken)
    {
        var response = await GetAsync("sesion", accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await ReadAsync(response)).GetProperty("codigo").GetString();

    private static string Challenge(JsonElement login) => login.GetProperty("desafio").GetString()!;

    private static string Code(string secret, DateTime? at = null) =>
        new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at ?? DateTime.UtcNow);

    private static async Task<string> ReadResetTokenAsync(string email)
    {
        var file = Directory.GetFiles(TestHost.EmailFolder)
            .Select(f => (Path: f, Text: File.ReadAllText(f)))
            .Single(f => f.Text.Contains(email));
        var match = System.Text.RegularExpressions.Regex.Match(file.Text, @"token=([A-Za-z0-9_\-]+)");
        match.Success.Should().BeTrue();
        await Task.CompletedTask;
        return match.Groups[1].Value;
    }
}
