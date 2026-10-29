using FluentValidation;

namespace MonitoreoTI.Application.Agents;

/// <summary>Datos que el agente envía al registrarse por primera vez (o al reinstalarse).</summary>
public sealed record EnrollRequest(
    string EnrollToken,
    string Hostname,
    string MacAddress,
    string? IpAddress,
    string? OperatingSystem,
    /// <summary>computadora, laptop o servidor; cualquier otro valor se trata como computadora.</summary>
    string? MachineType,
    string? Brand,
    string? Model,
    string? SerialNumber,
    string AgentVersion);

public sealed record EnrollResult(
    bool Succeeded,
    int MachineId = 0,
    int AgentId = 0,
    /// <summary>Credencial permanente en claro: se entrega una sola vez; el servidor solo guarda su hash.</summary>
    string? AgentCredential = null,
    /// <summary>Llave pública de firma de la empresa (Base64, null si la empresa aún no tiene llave).</summary>
    string? CompanyPublicKey = null,
    /// <summary>false = el equipo queda pendiente de aprobación y no puede conectarse todavía.</summary>
    bool Approved = true)
{
    public static EnrollResult Rejected { get; } = new(false);
}

public interface IAgentEnrollmentService
{
    /// <summary>
    /// Registra un agente con un token de sucursal. Un token inexistente, vencido o agotado se rechaza
    /// sin distinguir el motivo. El contador de usos se incrementa de forma atómica.
    /// </summary>
    Task<EnrollResult> EnrollAsync(EnrollRequest request, string? sourceIp, CancellationToken cancellationToken);
}

public sealed class EnrollRequestValidator : AbstractValidator<EnrollRequest>
{
    public EnrollRequestValidator()
    {
        RuleFor(x => x.EnrollToken).NotEmpty().MaximumLength(AgentTokens.MaxLength);
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MacAddress).NotEmpty().Must(mac => MacAddress.TryNormalize(mac, out _))
            .WithMessage("La MAC debe tener el formato AA:BB:CC:DD:EE:FF.");
        RuleFor(x => x.IpAddress).MaximumLength(45);
        RuleFor(x => x.OperatingSystem).MaximumLength(150);
        RuleFor(x => x.MachineType).MaximumLength(20);
        RuleFor(x => x.Brand).MaximumLength(100);
        RuleFor(x => x.Model).MaximumLength(100);
        RuleFor(x => x.SerialNumber).MaximumLength(100);
        RuleFor(x => x.AgentVersion).NotEmpty().MaximumLength(30);
    }
}
