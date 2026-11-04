using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MonitoreoTI.Api.Auth;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Tiempo real para el panel y la app (/hubs/tiempo-real). Grupos: una sucursal ("sucursal:{empresa}:{id}") para
/// los cambios de estado, y una máquina ("maquina:{empresa}:{id}") para las métricas, solo mientras su ficha está abierta.
/// El usuario entra solo a los grupos de sucursales que puede ver.
/// </summary>
[Authorize(Policy = PermissionPolicies.MachinesView)]
public sealed class RealtimeHub(IServiceProvider services) : Hub
{
    public const string Path = "/hubs/tiempo-real";

    public static string BranchGroup(int companyId, int branchId) => $"sucursal:{companyId}:{branchId}";
    public static string MachineGroup(int companyId, int machineId) => $"maquina:{companyId}:{machineId}";

    public override async Task OnConnectedAsync()
    {
        foreach (var branchId in await VisibleBranchIdsAsync())
            await Groups.AddToGroupAsync(Context.ConnectionId, BranchGroup(CompanyId, branchId));
        await base.OnConnectedAsync();
    }

    /// <summary>La ficha de un equipo se abrió: empezar a recibir sus métricas.</summary>
    public async Task SuscribirMaquina(int machineId)
    {
        await using var scope = CreateScope(out var queries);
        if (!await queries.CanSeeMachineAsync(machineId, Context.ConnectionAborted))
            throw new HubException("No tiene acceso a ese equipo.");
        await Groups.AddToGroupAsync(Context.ConnectionId, MachineGroup(CompanyId, machineId));
    }

    public Task DesuscribirMaquina(int machineId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, MachineGroup(CompanyId, machineId));

    private int CompanyId => AuthEndpoints.CompanyId(Context.User!) ?? throw new HubException("La sesión no tiene empresa.");

    private async Task<IReadOnlyCollection<int>> VisibleBranchIdsAsync()
    {
        await using var scope = CreateScope(out _);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var allowed = await user.AllowedBranchIdsAsync(Context.ConnectionAborted);
        if (allowed is not null)
            return allowed;

        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        return db.Branches.Select(b => b.Id).ToList();
    }

    /// <summary>
    /// Los métodos del hub no tienen un ámbito por petición como los endpoints: se crea uno con la empresa
    /// del token y el usuario de la conexión (en OnConnectedAsync no hay HttpContext disponible).
    /// </summary>
    private AsyncServiceScope CreateScope(out PanelQueries queries)
    {
        var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<Application.Abstractions.TenantContext>().SetCompany(CompanyId);
        scope.ServiceProvider.GetRequiredService<CurrentUser>().UsePrincipal(Context.User!);
        queries = scope.ServiceProvider.GetRequiredService<PanelQueries>();
        return scope;
    }
}

/// <summary>Envía los eventos del servidor a los grupos del hub.</summary>
public sealed class SignalRNotifier(IHubContext<RealtimeHub> hub) : IRealtimeNotifier
{
    public Task MachineStatusChangedAsync(int companyId, int branchId, int machineId, string status, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.BranchGroup(companyId, branchId))
            .SendAsync("maquinaEstadoCambio", new { maquinaId = machineId, sucursalId = branchId, estado = status }, cancellationToken);

    public Task MetricsReceivedAsync(int companyId, int machineId, MetricPoint point, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.MachineGroup(companyId, machineId))
            .SendAsync("metricasNuevas", new
            {
                maquinaId = machineId,
                fecha = point.At,
                cpu = point.Cpu,
                ram = point.Ram,
                disco = point.Disk,
                temperatura = point.Temperature,
            }, cancellationToken);

    public Task AlertChangedAsync(int companyId, int branchId, long alertId, int? machineId, bool isNew, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.BranchGroup(companyId, branchId))
            .SendAsync(isNew ? "alertaNueva" : "alertaActualizada",
                new { alertaId = alertId, maquinaId = machineId, sucursalId = branchId }, cancellationToken);

    public Task TicketChangedAsync(int companyId, int branchId, int ticketId, int? machineId, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.BranchGroup(companyId, branchId))
            .SendAsync("ticketActualizado", new { ticketId, maquinaId = machineId, sucursalId = branchId }, cancellationToken);

    public Task PortStatusChangedAsync(int companyId, int branchId, int deviceId, int? portId, string status, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.BranchGroup(companyId, branchId))
            .SendAsync("puertoEstadoCambio", new { dispositivoId = deviceId, puertoId = portId, estado = status, sucursalId = branchId }, cancellationToken);

    public Task CommandChangedAsync(int companyId, int branchId, long commandId, Guid? batchId, int machineId, string status, CancellationToken cancellationToken) =>
        hub.Clients.Group(RealtimeHub.BranchGroup(companyId, branchId))
            .SendAsync("comandoActualizado", new { comandoId = commandId, loteId = batchId, maquinaId = machineId, estado = status }, cancellationToken);
}

/// <summary>Nombres de las políticas por permiso que usa el panel.</summary>
public static class PermissionPolicies
{
    public static readonly string DashboardView = AuthExtensions.Permission(PermissionKeys.DashboardView);
    public const string MachinesView = AuthExtensions.PermissionPolicyPrefix + PermissionKeys.MachinesView;
    public static readonly string MachinesEdit = AuthExtensions.Permission(PermissionKeys.MachinesEdit);
    public static readonly string BranchesEdit = AuthExtensions.Permission(PermissionKeys.BranchesEdit);
    public static readonly string InstallersGenerate = AuthExtensions.Permission(PermissionKeys.InstallersGenerate);
    public static readonly string AlertsView = AuthExtensions.Permission(PermissionKeys.AlertsView);
    public static readonly string AlertsManage = AuthExtensions.Permission(PermissionKeys.AlertsManage);
    public static readonly string TicketsView = AuthExtensions.Permission($"{PermissionKeys.TicketsCreate}|{PermissionKeys.TicketsManage}");
    public static readonly string TicketsManage = AuthExtensions.Permission(PermissionKeys.TicketsManage);
    public static readonly string NetworkView = AuthExtensions.Permission(PermissionKeys.NetworkView);
    public static readonly string NetworkEdit = AuthExtensions.Permission(PermissionKeys.NetworkEdit);
    public static readonly string UsersAdmin = AuthExtensions.Permission(PermissionKeys.UsersAdmin);
    public static readonly string SettingsEdit = AuthExtensions.Permission(PermissionKeys.SettingsEdit);
    public static readonly string ReportsView = AuthExtensions.Permission(PermissionKeys.ReportsView);
    public static readonly string RemoteAny = AuthExtensions.Permission(
        $"{PermissionKeys.RemoteConnect}|{PermissionKeys.RemoteTerminal}|{PermissionKeys.RemoteFiles}");
    public static readonly string ScriptsView = AuthExtensions.Permission($"{PermissionKeys.ScriptsRun}|{PermissionKeys.ScriptsEdit}");
    public static readonly string ScriptsRun = AuthExtensions.Permission(PermissionKeys.ScriptsRun);
    public static readonly string ScriptsEdit = AuthExtensions.Permission(PermissionKeys.ScriptsEdit);
}
