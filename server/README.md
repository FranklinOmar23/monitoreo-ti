# Servidor

Solución `MonitoreoTI.sln` (ASP.NET Core 8, Clean Architecture).

| Proyecto | Depende de | Contenido |
| --- | --- | --- |
| `MonitoreoTI.Domain` | — | Entidades (nombres en inglés) y enums con su valor de base de datos en `[EnumMember]` |
| `MonitoreoTI.Application` | Domain | Casos de uso; `ITenantContext` (empresa del ámbito actual) |
| `MonitoreoTI.Infrastructure` | Application | EF Core: `MonitoreoDbContext`, mapeo a las tablas en español, filtro por empresa, interceptor de RLS |
| `MonitoreoTI.Workers` | Application | Servicios en segundo plano (Fase 1+) |
| `MonitoreoTI.Api` | Infrastructure, Workers | API, `/health` |

## Configuración (User Secrets, nunca en el repositorio)

```powershell
dotnet user-secrets set "ConnectionStrings:MonitoreoTI" "Server=<servidor>,1433;Database=MonitoreoTI;User Id=monitoreoti_app;Password=<...>;TrustServerCertificate=True;Encrypt=True" --project src/MonitoreoTI.Api

# Pruebas de integración: conexión al servidor SIN base, con un login que pueda crear bases
dotnet user-secrets set "ConnectionStrings:TestServer" "Server=<servidor>,1433;User Id=sa;Password=<...>;TrustServerCertificate=True;Encrypt=True" --project tests/MonitoreoTI.Api.IntegrationTests
```

También se aceptan las variables de entorno `ConnectionStrings__MonitoreoTI` y `ConnectionStrings__TestServer`.

```powershell
dotnet build
dotnet test                                   # recrea la base MonitoreoTI_Tests desde ../database
dotnet run --project src/MonitoreoTI.Api      # http://localhost:5126/health
dotnet watch --project src/MonitoreoTI.Api --launch-profile http --no-hot-reload   # se reinicia sola al guardar (o `npm run dev:api` en la raíz)
```

Sin `ConnectionStrings:TestServer` las pruebas que necesitan SQL Server se omiten.

**Swagger** (solo en desarrollo): http://localhost:5126/swagger lista todos los endpoints agrupados por recurso. Para probarlos, haga login con `POST /api/v1/auth/login`, copie el `accessToken` y péguelo en **Authorize**.

## Aislamiento entre empresas

1. **SQL Server RLS**: `TenantSessionContextInterceptor` ejecuta `sp_set_session_context` (solo lectura) al abrir cada conexión.
2. **EF Core**: filtro global por `CompanyId` en toda entidad `ITenantEntity` / `IOptionalTenantEntity`.
3. **Escritura**: `SaveChanges` asigna `CompanyId` desde el contexto; rechaza escribir en otra empresa o cambiar la empresa de un registro (`TenantViolationException`).
4. **FK compuestas** en la base.

`TenantContext` se fija una sola vez por ámbito. Los trabajos en segundo plano crean un ámbito por empresa.
