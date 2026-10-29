namespace MonitoreoTI.Infrastructure.Persistence;

/// <summary>
/// Intento de leer o escribir datos fuera de la empresa del contexto.
/// Debe registrarse en auditoría y alertar al superadministrador.
/// </summary>
public sealed class TenantViolationException(string message) : InvalidOperationException(message);
