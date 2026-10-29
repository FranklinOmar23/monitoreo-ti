# Agente

Electron + TypeScript, construido con electron-vite. Un solo instalador con dos modos de ejecución:

| Modo | Entrada | Cómo corre | Responsabilidad |
| --- | --- | --- | --- |
| Servicio | `src/service/index.ts` → `out/main/service.js` | `electron.exe` con `ELECTRON_RUN_AS_NODE=1`, lanzado por el anfitrión del servicio de Windows (LocalSystem). Arranca antes del inicio de sesión | Conexión WSS, recolección, ejecutor, cola local, SNMP, actualizaciones |
| UI | `src/main/index.ts` → `out/main/index.js` | Electron normal en la sesión del usuario | Reemplaza al SessionHelper: captura de pantalla, mouse y teclado, aviso de sesión remota, bandeja |

El modo servicio solo usa APIs de Node (nunca el módulo `electron`).

```powershell
npm run build -w @monitoreoti/agent
npm run service -w @monitoreoti/agent        # modo servicio en consola (Node)
npm run dev -w @monitoreoti/agent            # modo UI con recarga
npm test -w @monitoreoti/agent
```

## Configuración

Instalado, el agente lee `config.json` en su carpeta de datos (lo escribe el instalador). En desarrollo se pueden usar
variables de entorno, que tienen prioridad:

| Variable | Uso |
| --- | --- |
| `MONITOREOTI_SERVER_URL` | Servidor (por defecto `http://localhost:5126`; fuera de localhost exige HTTPS) |
| `MONITOREOTI_ENROLL_TOKEN` | Token de registro de la sucursal; solo hace falta hasta obtener la credencial |
| `MONITOREOTI_DATA_DIR` | Carpeta de la credencial cifrada, la cola local y la bitácora (por defecto `%ProgramData%\MonitoreoTI\Agent`) |
| `MONITOREOTI_HEARTBEAT_SECONDS` | Heartbeat inicial; luego manda el valor de la `bienvenida` del servidor |

Al obtener su credencial, el agente borra el token de `config.json`.

## Cómo funciona el modo servicio

1. **Registro** (`bootstrap.ts`, `enroll.ts`): si no hay credencial guardada, se registra con el token de la sucursal y
   guarda la credencial cifrada con DPAPI (`credential-store.ts`). Si falta el token o el servidor no responde, espera y
   reintenta en vez de terminar.
2. **Conexión** (`gateway-client.ts`): WebSocket saliente a `/agent/ws` con `Authorization: Bearer`, saludo `hola`,
   heartbeat, y reconexión con espera exponencial (1 s a ~1 min, ±20 %).
3. **Recolección** (`collectors/`): CPU, RAM y disco con Node cada minuto; temperatura y discos por WMI cada pocos
   minutos; hardware, software (registro `Uninstall`) e interfaces de red por PowerShell.
4. **Cola local** (`outbox.ts`, `sender.ts`): todo mensaje pasa por SQLite y se borra solo con el `ack` del servidor.
   Sin conexión se acumula (máximo 10 000; se descartan primero las métricas más viejas).

## Probar contra el API local

```powershell
$env:ELECTRON_RUN_AS_NODE = '1'
$env:MONITOREOTI_ENROLL_TOKEN = '1.DEV-ENROLL-SANTO-DOMINGO'   # token del seed (Caribe, Santo Domingo)
$env:MONITOREOTI_DATA_DIR = "$env:TEMP\monitoreoti-agent"
npm run build -w @monitoreoti/agent
..\node_modules\electron\dist\electron.exe out\main\service.js
```

## Instalador (.exe)

```powershell
npm run installer -w @monitoreoti/agent      # genera agent/release/MonitoreoTI-Agente-Setup.exe
```

Requiere el SDK de .NET 8 (para publicar el anfitrión); la primera vez electron-builder descarga NSIS.

- **Interactivo**: pide la dirección del servidor y el token de instalación (se crea en el panel, en Instaladores).
- **Silencioso** (como administrador): `MonitoreoTI-Agente-Setup.exe /S /SERVER=https://servidor /TOKEN=<token>`.
- **Actualizar**: volver a ejecutar el instalador; conserva la credencial y la cola local.
- **Desinstalar**: desde "Aplicaciones" de Windows; quita el servicio y borra la carpeta de datos.

`host/MonitoreoTI.AgentHost` es el anfitrión del servicio de Windows (.NET 8, un solo .exe autocontenido): crea el
servicio `MonitoreoTIAgent`, limita `%ProgramData%\MonitoreoTI\Agent` a SYSTEM y administradores, escribe `config.json`,
lanza el agente y lo reinicia si se cae. La bitácora queda en `%ProgramData%\MonitoreoTI\Agent\logs\agente.log`.

Para que el panel ofrezca la descarga, el servidor debe tener `Instaladores:Archivo` apuntando al .exe.

Probar el anfitrión sin instalar el servicio (no requiere administrador):

```powershell
$env:MONITOREOTI_DATA_DIR = "$env:TEMP\monitoreoti-agent"
.\release\win-unpacked\resources\host\MonitoreoTI.AgentHost.exe run
```
