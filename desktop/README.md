# Panel de administración

Electron 45 + React 18 + TypeScript, construido con electron-vite (Vite 8).

```powershell
npm run dev -w @monitoreoti/desktop        # desarrollo con recarga
npm run build -w @monitoreoti/desktop      # genera out/
npm test -w @monitoreoti/desktop
```

`MONITOREOTI_SERVER_URL` indica el servidor (por defecto `http://localhost:5126`); la CSP solo permite conectarse a él.

| Carpeta | Contenido |
| --- | --- |
| `src/main` | Proceso principal: ventana, seguridad (`security.ts`: CSP y navegación), IPC validado |
| `src/preload` | `contextBridge`: solo expone `window.monitoreo` (`bridge.ts`) |
| `src/renderer` | Interfaz React |
| `src/components` | Componentes propios reutilizables (alias `@components`) |

Seguridad (sección 12.1): `contextIsolation`, `sandbox`, sin `nodeIntegration`, CSP sin `eval`, navegación y ventanas
emergentes bloqueadas, permisos del navegador denegados.
