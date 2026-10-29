using FluentValidation;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Api.Agents;

public static class AgentEndpoints
{
    public const string RegisterRateLimit = "agent-register";

    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/agent/register", RegisterAsync).RequireRateLimiting(RegisterRateLimit);
    }

    private static async Task<IResult> RegisterAsync(
        EnrollRequest request,
        IValidator<EnrollRequest> validator,
        IAgentEnrollmentService enrollment,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var result = await enrollment.EnrollAsync(request, http.Connection.RemoteIpAddress?.ToString(), cancellationToken);

        // Inexistente, vencido o agotado: misma respuesta, para no revelar qué tokens existen.
        if (!result.Succeeded)
            return Results.Problem(
                title: "Token de registro inválido o vencido.",
                statusCode: StatusCodes.Status401Unauthorized);

        return Results.Ok(new
        {
            machineId = result.MachineId,
            agentId = result.AgentId,
            credential = result.AgentCredential,
            companyPublicKey = result.CompanyPublicKey,
            aprobado = result.Approved,
        });
    }
}
