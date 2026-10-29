# shared

Paquete TypeScript `@monitoreoti/shared`, usado por `desktop`, `mobile` y `agent`. Se publica como código fuente
TypeScript: Vite y Metro lo compilan, y electron-vite lo incluye en el bundle (no se externaliza).

| Archivo           | Contenido                                                  |
| ----------------- | ---------------------------------------------------------- |
| `src/status.ts`   | Estados del semáforo y de puertos con sus colores y textos |
| `src/protocol.ts` | Sobre de mensajes agente ↔ servidor (sección 9)            |

Más adelante: cliente API, tipos de DTO y validaciones (zod).

```powershell
npm test -w @monitoreoti/shared
```
