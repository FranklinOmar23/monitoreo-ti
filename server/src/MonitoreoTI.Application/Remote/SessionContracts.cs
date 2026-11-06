using FluentValidation;

namespace MonitoreoTI.Application.Remote;

// ------------------------------------------------------------------ sesiones remotas y relay (sección 10.1)

/// <param name="Tipo">terminal, archivos o escritorio.</param>
/// <param name="Motivo">Por qué se conecta (queda en el historial y en la auditoría).</param>
/// <param name="SoloVer">Escritorio sin control de teclado ni mouse.</param>
/// <param name="PedirPermiso">Pedir permiso al usuario en esta sesión (si la empresa lo exige, se pide siempre).</param>
public sealed record SessionInput(int MaquinaId, string Tipo, string? Motivo = null, bool SoloVer = false, bool PedirPermiso = false);

public sealed class SessionInputValidator : AbstractValidator<SessionInput>
{
    public SessionInputValidator()
    {
        RuleFor(x => x.MaquinaId).GreaterThan(0).WithMessage("Elija el equipo.");
        RuleFor(x => x.Tipo).Must(SessionRules.IsType).WithMessage("El tipo es terminal, archivos o escritorio.");
        RuleFor(x => x.Motivo).MaximumLength(300);
    }
}

/// <summary>
/// Lo que recibe el panel al abrir una sesión: se conecta a <paramref name="RelayPath"/> (relativo al servidor) y manda
/// <paramref name="Token"/> como primer mensaje. El token sirve una sola vez y vence a los 60 segundos.
/// </summary>
public sealed record SessionTicket(long SessionId, string Type, int MachineId, string MachineName, string RelayPath, string Token, DateTime ExpiresAt);

/// <summary>Una sesión del historial (W-34). Duración en segundos; null si sigue abierta.</summary>
public sealed record SessionItem(
    long Id, string Type, int MachineId, string MachineName, string BranchName, int UserId, string UserName,
    DateTime StartedAt, DateTime? EndedAt, int? DurationSeconds, string? SourceIp, bool Active, string? Reason = null, bool ViewOnly = false);

/// <summary>Historial de sesiones (decisión 83): filtros por cada campo y página de 10.</summary>
public sealed record SessionFilter(
    int? MachineId = null, int? UserId = null, string? Type = null, bool? ViewOnly = null, bool? Active = null,
    DateTime? From = null, DateTime? To = null, int Page = 1, int Limit = Panel.Paging.DefaultLimit);

/// <summary>Abre y cierra sesiones en el relay; implementado en el API (tiene las conexiones de los agentes).</summary>
public interface IRemoteRelay
{
    /// <summary>
    /// Registra la sesión en el relay, avisa al agente ("abrir_sesion_remota") y devuelve el token del panel.
    /// null si el agente del equipo no está conectado ahora.
    /// </summary>
    Task<(string PanelToken, DateTime ExpiresAt)?> OpenAsync(RelayOpening opening, CancellationToken cancellationToken);

    /// <summary>Corta la sesión (si sigue abierta) y avisa al agente. false si no estaba en el relay.</summary>
    Task<bool> CloseAsync(int companyId, long sessionId, string reason, CancellationToken cancellationToken);

    bool IsActive(long sessionId);
}

/// <summary>
/// "actividad_sesion" del agente: lo que el técnico hizo con archivos en una sesión (ruta y tamaño, nunca el contenido).
/// Accion: descargar, subir, crear_carpeta, renombrar o eliminar.
/// </summary>
public sealed record SessionActivityData(long SesionId, string Accion, string? Ruta, string? Destino, long? Tamano);

/// <param name="TechnicianName">Se muestra en el aviso del equipo ("Soporte de TI está conectado...").</param>
/// <param name="AskConsent">El usuario del equipo debe aceptar (escritorio).</param>
/// <param name="ViewOnly">Escritorio sin control: el asistente ignora teclado y mouse.</param>
/// <param name="AllowFiles">El técnico tiene remoto.archivos: puede transferir archivos dentro del escritorio.</param>
public sealed record RelayOpening(
    long SessionId, int CompanyId, int MachineId, int UserId, string Type, string TechnicianName = "", bool AskConsent = false,
    bool ViewOnly = false, bool AllowFiles = false);

public sealed record RemoteSettings(bool PedirPermiso);

public static class SessionRules
{
    /// <summary>El agente clásico (Windows 7 y 8, versión "-clasico") abre sesiones remotas desde la 0.2.0 (decisión 84).</summary>
    public static readonly Version ClassicSessionsVersion = new(0, 2, 0);

    /// <summary>
    /// Agente clásico anterior a la 0.2.0: no tiene escritorio remoto, terminal ni archivos e ignoraría la orden. Con el
    /// clásico 0.2.0 o posterior las sesiones son iguales a las del agente normal.
    /// </summary>
    public static bool IsClassicWithoutSessions(string? agentVersion)
    {
        if (agentVersion?.Contains("clasico", StringComparison.OrdinalIgnoreCase) != true)
            return false;
        var number = agentVersion.Split('-')[0];
        return !Version.TryParse(number, out var version) || version < ClassicSessionsVersion;
    }

    /// <summary>Vigencia de los tokens de un solo uso y espera máxima a que llegue la otra parte.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);

    public static bool IsType(string? value) => value is "terminal" or "archivos" or "escritorio";

    /// <summary>Permiso que exige cada tipo de sesión (sección 7.1).</summary>
    public static string Permission(string type) => type switch
    {
        "terminal" => "remoto.terminal",
        "archivos" => "remoto.archivos",
        _ => "remoto.conectar",
    };

    public static string RelayPath(long sessionId) => $"/relay/{sessionId}";
}
