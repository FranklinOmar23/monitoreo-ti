using System.Security.Cryptography;
using System.Text;
using FluentValidation;

namespace MonitoreoTI.Application.Remote;

// ------------------------------------------------------------------ firma (sección 5 y decisión 50)

/// <summary>
/// Texto exacto que firma el servidor con la llave ECDSA P-256 de la empresa y que el agente reconstruye para
/// verificar (espejo de agent/src/service/executor.ts). Incluye el equipo y el vencimiento: una firma no sirve
/// para otro equipo ni después de vencer. El contenido va por su SHA-256 para que el texto firmado sea corto.
/// </summary>
public static class CommandSigning
{
    public const string CommandPrefix = "monitoreoti/comando/v1";
    public const string ScriptPrefix = "monitoreoti/script/v1";

    public static string CommandPayload(long commandId, int machineId, string type, string language, int timeoutSeconds, string expiresAt, string content) =>
        string.Join('\n', CommandPrefix, commandId, machineId, type, language, timeoutSeconds, expiresAt, Sha256Hex(content));

    /// <summary>Firma guardada con el script: si alguien cambia el contenido en la base, deja de ser válida y no se ejecuta.</summary>
    public static string ScriptPayload(int companyId, string language, string content) =>
        string.Join('\n', ScriptPrefix, companyId, language, Sha256Hex(content));

    /// <summary>Formato de fecha del texto firmado (UTC, sin fracciones), el mismo que viaja en "vence".</summary>
    public static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string Sha256Hex(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Firma con la llave de la empresa del contexto actual; la crea la primera vez que se necesita.</summary>
public interface ICompanySigner
{
    /// <summary>Llave pública (SubjectPublicKeyInfo en Base64) que se entrega a los agentes.</summary>
    Task<string> PublicKeyAsync(CancellationToken cancellationToken);

    /// <summary>Firma ECDSA P-256 / SHA-256 en formato IEEE P1363 (r|s), en Base64.</summary>
    Task<string> SignAsync(string payload, CancellationToken cancellationToken);

    Task<bool> VerifyAsync(string payload, string signature, CancellationToken cancellationToken);
}

/// <summary>Envía a los agentes conectados sus comandos pendientes; los desconectados los reciben al volver.</summary>
public interface ICommandDispatcher
{
    Task DispatchAsync(int companyId, IReadOnlyCollection<long> commandIds, CancellationToken cancellationToken);
}

/// <summary>Por qué se niega el acceso remoto aunque el usuario tenga el permiso.</summary>
public enum RemoteAccessDenial
{
    /// <summary>El último 2FA del técnico tiene más de 8 horas (decisión 52): pedir el código otra vez.</summary>
    TwoFactorRequired,
    /// <summary>El plan de la empresa no incluye el módulo remoto.</summary>
    ModuleNotIncluded,
}

// ------------------------------------------------------------------ biblioteca de scripts (W-33)

public sealed record ScriptInput(string Nombre, string Lenguaje, string? Descripcion, string Contenido);

public sealed record ScriptItem(
    int Id, string Name, string Language, string? Description, string Content, string CreatedBy, DateTime CreatedAt, DateTime? UpdatedAt,
    bool Signed, DateTime? LastRunAt);

public sealed class ScriptInputValidator : AbstractValidator<ScriptInput>
{
    public ScriptInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre.").MaximumLength(150);
        RuleFor(x => x.Lenguaje).Must(RemoteRules.IsLanguage).WithMessage("El lenguaje es powershell, cmd, bash o python.");
        RuleFor(x => x.Descripcion).MaximumLength(500);
        RuleFor(x => x.Contenido).NotEmpty().WithMessage("Escriba el contenido.").MaximumLength(RemoteRules.MaxContentLength);
    }
}

// ------------------------------------------------------------------ ejecutar (W-32) y resultados

/// <summary>Un script de la biblioteca (ScriptId) o un comando escrito en el momento (Lenguaje y Contenido).</summary>
public sealed record RunInput(IReadOnlyList<int> MaquinaIds, int? ScriptId, string? Lenguaje, string? Contenido, int? TimeoutS);

public sealed record RunResult(Guid BatchId, int Count);

public sealed class RunInputValidator : AbstractValidator<RunInput>
{
    public RunInputValidator()
    {
        RuleFor(x => x.MaquinaIds).NotEmpty().WithMessage("Elija al menos un equipo.")
            .Must(ids => ids is null || ids.Count <= RemoteRules.MaxMachinesPerRun).WithMessage($"Como máximo {RemoteRules.MaxMachinesPerRun} equipos a la vez.");
        RuleFor(x => x).Must(x => x.ScriptId is not null || !string.IsNullOrWhiteSpace(x.Contenido))
            .OverridePropertyName("Contenido").WithMessage("Elija un script o escriba el comando.");
        RuleFor(x => x.Lenguaje).Must(l => l is null || RemoteRules.IsLanguage(l)).WithMessage("El lenguaje es powershell, cmd, bash o python.");
        RuleFor(x => x).Must(x => x.ScriptId is not null || x.Lenguaje is not null)
            .OverridePropertyName("Lenguaje").WithMessage("Elija el lenguaje del comando.");
        RuleFor(x => x.Contenido).MaximumLength(RemoteRules.MaxContentLength);
        RuleFor(x => x.TimeoutS).InclusiveBetween(10, RemoteRules.MaxTimeoutSeconds)
            .WithMessage($"El tiempo límite va de 10 a {RemoteRules.MaxTimeoutSeconds} segundos.");
    }
}

/// <summary>Una ejecución (lote) en uno o varios equipos, con el avance de sus comandos.</summary>
public sealed record RunSummary(
    Guid BatchId, string Title, string Language, string UserName, DateTime CreatedAt, int Total, int Pending, int Running, int Completed, int Failed);

/// <param name="Status">pendiente, enviado, ejecutando, completado, error o expirado.</param>
public sealed record CommandItem(
    long Id, Guid? BatchId, int MachineId, string MachineName, string BranchName, string Status, int? ExitCode,
    DateTime CreatedAt, DateTime? SentAt, DateTime? CompletedAt, string? Output);

public sealed record RunDetail(RunSummary Summary, string Content, int TimeoutSeconds, IReadOnlyList<CommandItem> Commands);

/// <summary>"resultado_comando" del agente. Estado: ejecutando, completado, error o rechazado (firma, vencido, repetido).</summary>
public sealed record CommandResultData(long ComandoId, string Estado, int? CodigoSalida, string? Salida, string? Error);

public static class RemoteRules
{
    public const int MaxContentLength = 100_000;
    public const int MaxMachinesPerRun = 200;
    public const int DefaultTimeoutSeconds = 300;
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>La salida que se guarda (el agente ya la recorta a 1 MB; aquí se limita a lo que conviene mostrar).</summary>
    public const int MaxStoredOutput = 1_000_000;

    /// <summary>Un comando sin entregar vence a las 24 h: un equipo que vuelve una semana después no lo ejecuta.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    /// <summary>Vigencia del último 2FA para el acceso remoto (decisión 52).</summary>
    public static readonly TimeSpan TwoFactorWindow = TimeSpan.FromHours(8);

    public static bool IsLanguage(string? value) => value is "powershell" or "cmd" or "bash" or "python";

    /// <summary>Título de una ejecución: el nombre del script o la primera línea del comando.</summary>
    public static string Title(string? scriptName, string content)
    {
        if (!string.IsNullOrWhiteSpace(scriptName))
            return scriptName;
        var line = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length > 80 ? line[..77] + "..." : line;
    }
}
