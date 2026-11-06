# MonitoreoTI

Sistema propio de monitoreo y gestión remota de equipos de TI (RMM), con módulo de red y data center en 3D, multiempresa.

La especificación completa está en [ESPECIFICACION.md](ESPECIFICACION.md) (ver la sección 19 para las decisiones tomadas durante el desarrollo).

| Carpeta | Contenido |
| --- | --- |
| [database/](database/) | Scripts SQL Server (base, esquema multiempresa, Row-Level Security, datos de prueba) |
| [server/](server/) | API ASP.NET Core 8 (Clean Architecture), gateway de agentes, relay |
| [agent/](agent/) | Agente en Electron: modo servicio de Windows y modo UI en la sesión del usuario |
| [desktop/](desktop/) | Panel de administración (Electron + React 18 + TypeScript + Vite) |
| [mobile/](mobile/) | App móvil (React Native + Expo) |
| [shared/](shared/) | Paquete TypeScript compartido (estados y colores, protocolo del agente) |

## Requisitos de desarrollo

- .NET SDK 8 (fijado en [global.json](global.json))
- Node.js 20+ y npm
- SQL Server 2019+ y `sqlcmd`

## Primeros pasos

```powershell
# 1. Base de datos (ver database/README.md)
$env:MONITOREOTI_SQL_PASSWORD = '...'; $env:MONITOREOTI_APP_PASSWORD = '...'
./database/reset-db.ps1 -Server <servidor>,1433 -User sa

# 2. Servidor (ver server/README.md para los User Secrets)
cd server; dotnet test; dotnet run --project src/MonitoreoTI.Api   # http://localhost:5126/health
npm run dev:api   # la API se reinicia sola al guardar un .cs (dotnet watch)

# 3. Clientes TypeScript (desde la raíz)
npm install
node node_modules/electron/install.js   # descarga el binario de Electron la primera vez
npm test; npm run typecheck; npm run lint
npm run dev:desktop      # panel
npm run dev:agent        # agente, modo UI
npm run start:mobile     # app (Expo)
```

> **VS Code:** su terminal integrada define `ELECTRON_RUN_AS_NODE=1`, que hace que Electron arranque como Node
> sin ventanas. Si el panel o el agente fallan con `Cannot read properties of undefined (reading 'isPackaged')`,
> ejecute antes `Remove-Item Env:ELECTRON_RUN_AS_NODE`.
