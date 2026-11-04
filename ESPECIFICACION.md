# Especificación para Claude Code — Sistema MonitoreoTI

> **Cómo usar este archivo:** guárdalo en la raíz del repositorio como `ESPECIFICACION.md`, copia el script `monitoreo_ti_sqlserver.sql` en `database/` y dile a Claude Code:
> *"Lee ESPECIFICACION.md completo y empieza por la Fase 0. Antes de escribir código, preséntame el plan de la fase."*

---

## 1. Tu rol y forma de trabajar

Eres el desarrollador principal de **MonitoreoTI**, un sistema propio de monitoreo y gestión remota de equipos de TI (tipo RMM), con módulo de red y data center en 3D, multiempresa (multi-tenant).

Reglas de trabajo:

1. **Trabaja por fases** (sección 17). Al empezar cada fase, presenta un plan corto (archivos a crear, decisiones, dudas) y espera mi aprobación. Al terminar, resume qué se hizo, cómo probarlo y qué quedó pendiente.
2. **No avances a la siguiente fase** sin que yo lo confirme.
3. **Todo el código del producto es propio.** No se usan plataformas de terceros de monitoreo, RMM o control remoto (nada de Tactical RMM, MeshCentral, Zabbix, TeamViewer, AnyDesk, RustDesk, etc.). Sí se permiten **librerías** de código abierto para tareas puntuales (lista en la sección 4.3); si necesitas otra, pregúntame primero.
4. Idioma: **código, nombres de clases, variables y endpoints en inglés**; **textos de la interfaz, mensajes al usuario y comentarios de negocio en español** (República Dominicana). Las tablas y columnas de la base de datos se mantienen en español como en el script existente.
5. Cada fase incluye pruebas automáticas de la lógica importante (sección 16).
6. Nunca guardes secretos en el código ni en el repositorio: usa variables de entorno, `appsettings.Development.json` ignorado por git, o User Secrets de .NET.
7. Si algo de esta especificación es ambiguo o contradictorio, pregúntame en vez de suponer.

---

## 2. Qué es el producto

- Cada computadora de una empresa tiene un **agente** instalado que reporta su salud (CPU, RAM, discos, salud SMART, temperatura), errores del sistema, procesos, servicios, software instalado y tarjetas de red.
- Cada equipo tiene una **etiqueta QR**. Un técnico la escanea con la **app móvil** y ve al instante el estado de ese equipo: qué componentes fallan, qué errores tiene y qué procesos corren.
- Un **panel de administración de escritorio** (Electron + React) permite administrar todo: equipos, sucursales, alertas, tickets, usuarios, red y data center.
- Desde el panel (y en parte desde la app) el técnico puede **conectarse de forma remota** al equipo: terminal, explorador de archivos y escritorio remoto, todo desarrollado en casa.
- El **módulo de red y data center** lee los switches por SNMP y muestra gabinetes y switches en **3D**, en vivo: qué máquina e IP hay en cada puerto y qué puertos tienen problemas. La asociación PC ↔ puerto es automática por MAC.
- Es **multi-tenant**: varias empresas usan el mismo sistema, cada una totalmente aislada, con planes y límites.
- Las empresas tienen **sucursales en todo el país**; el agente se instala con un instalador o token propio de cada sucursal.
- Semáforo de estado usado en todo el sistema: **verde** = sano, **amarillo** = advertencia, **rojo** = crítico, **gris** = desconectado.

---

## 3. Arquitectura general

```
[Agente en cada PC] --WSS saliente 443--> [Servidor central .NET] <--HTTPS + SignalR-- [Panel escritorio Electron]
[Agente recolector] --SNMPv3--> [Switches / router / impresoras de su sucursal]          <--HTTPS + SignalR-- [App móvil React Native]
                                         |
                                   [SQL Server]
```

Principios fijos:

- **El agente siempre inicia la conexión hacia afuera** (WebSocket seguro por el puerto 443). Así funciona detrás del router de cualquier sucursal sin abrir puertos ni VPN.
- **El panel de escritorio y la app móvil son solo clientes** de la API. **Nunca se conectan directo a SQL Server.**
- Toda la lógica de negocio, permisos y aislamiento entre empresas vive en el servidor.
- El servidor nunca entra a la red de una sucursal: lo que hay que leer allí (switches, impresoras) lo lee un **agente recolector** de esa sucursal y lo envía por su WebSocket.
- El acceso remoto pasa por un **relay** en el servidor: agente y panel se conectan al relay; ninguno necesita IP pública.

---

## 4. Tecnologías

### 4.1 Stack

| Pieza | Tecnología |
| --- | --- |
| Agente | C# .NET 8 Worker Service, publicado con Native AOT (un solo .exe), servicio de Windows |
| Servidor | ASP.NET Core 8, Clean Architecture |
| Gateway de agentes y relay remoto | ASP.NET Core con WebSockets nativos (`System.Net.WebSockets`) |
| Tiempo real a clientes | SignalR |
| Base de datos | SQL Server 2019+ (base `MonitoreoTI`), Entity Framework Core 8 |
| Trabajos en segundo plano | `BackgroundService` / Hosted Services de .NET |
| Panel de administración | Electron + React 18 + TypeScript + Vite |
| Vista 3D | Three.js (con React Three Fiber si conviene) |
| App móvil | React Native con Expo + TypeScript |
| Código compartido TS | Paquete `shared` en el monorepo (tipos, cliente API, constantes) |

### 4.2 Estructura del monorepo

```
monitoreo-ti/
├── ESPECIFICACION.md
├── database/
│   ├── 01_schema.sql              (script base existente)
│   ├── 02_multitenant.sql         (cambios de la sección 6.2)
│   └── 03_seed_dev.sql            (datos de prueba)
├── server/
│   ├── MonitoreoTI.sln
│   ├── src/
│   │   ├── MonitoreoTI.Domain/          entidades, enums, reglas puras
│   │   ├── MonitoreoTI.Application/     casos de uso, DTOs, interfaces, validaciones
│   │   ├── MonitoreoTI.Infrastructure/  EF Core, SQL Server, correo, push, cifrado, SNMP helpers
│   │   ├── MonitoreoTI.Api/             API REST, autenticación, SignalR hubs, gateway de agentes, relay
│   │   └── MonitoreoTI.Workers/         motor de alertas, correlación MAC, detector de desconexión (puede ir dentro de Api al inicio)
│   └── tests/
├── agent/
│   ├── MonitoreoTI.Agent/               servicio principal
│   ├── MonitoreoTI.Agent.SessionHelper/ proceso en la sesión del usuario (pantalla y entrada)
│   ├── MonitoreoTI.Agent.Shared/        protocolo de mensajes compartido con el servidor
│   ├── installer/                       proyecto del instalador .msi
│   └── tests/
├── desktop/                             Electron + React
├── mobile/                              React Native (Expo)
└── shared/                              TypeScript: tipos, cliente API, colores de estado, validaciones
```

El protocolo de mensajes agente ↔ servidor se define en C# en `MonitoreoTI.Agent.Shared` y lo referencian el agente y el servidor.

### 4.3 Librerías permitidas (sin preguntar)

- .NET: EF Core, `Microsoft.Data.SqlClient`, `Microsoft.Data.Sqlite` (cola local del agente), `System.Management` (WMI), `FluentValidation`, `Serilog`, `BCrypt.Net-Next` o Argon2 (`Konscious.Security.Cryptography`), `Otp.NET` (2FA TOTP), `QRCoder`, `Lextm.SharpSnmpLib` (SNMP v2c/v3), `xUnit`, `FluentAssertions`, `NSubstitute`.
- Escritorio: `electron`, `electron-builder`, `electron-updater` (actualizaciones servidas desde nuestro propio servidor), `react-router`, `@tanstack/react-query`, `zustand`, `@microsoft/signalr`, `three`, `@react-three/fiber`, `@react-three/drei`, `xterm` (`@xterm/xterm`), `recharts`, `zod`, `react-hook-form`, `vitest`, `@testing-library/react`, `playwright`.
- Móvil: `expo`, `expo-camera` (QR), `expo-secure-store`, `expo-local-authentication`, `expo-notifications`, `@tanstack/react-query`, `@microsoft/signalr`, `react-navigation`.
- Instalador del agente: WiX Toolset (herramienta de compilación del .msi).

---

## 5. Multi-tenant (aislamiento entre empresas)

Este sistema ejecuta comandos y abre sesiones remotas en computadoras: **un error de aislamiento es un incidente de seguridad grave**. El aislamiento se implementa en cuatro capas, todas obligatorias:

1. **Datos:** todas las tablas de negocio tienen `empresa_id NOT NULL` (sección 6.2).
2. **SQL Server Row-Level Security:** política que filtra por `SESSION_CONTEXT(N'empresa_id')`. Un `DbConnectionInterceptor` de EF Core ejecuta `sp_set_session_context` al abrir cada conexión con el `empresa_id` del usuario o agente autenticado. El superadministrador usa `SESSION_CONTEXT(N'es_superadmin') = 1` para saltar el filtro solo en el portal del proveedor.
3. **EF Core:** filtro global `HasQueryFilter(e => e.EmpresaId == _tenant.EmpresaId)` en todas las entidades con empresa. Al insertar, el `empresa_id` lo asigna el servidor, **nunca** se toma del cuerpo de la petición.
4. **Lógica:** antes de enviar un comando, abrir una sesión remota o leer datos de una máquina, el servidor verifica que **usuario, máquina y agente pertenezcan a la misma empresa**. Si no coinciden: rechazar, registrar en auditoría y alertar al superadministrador.

Reglas adicionales:

- El JWT incluye el claim `empresa_id`. Si un usuario pertenece a varias empresas (`usuario_empresa`), elige una al iniciar sesión y se emite un token para esa empresa.
- Cada token de registro de agente pertenece a una empresa y a una sucursal; el agente queda amarrado a esa empresa para siempre.
- Cada empresa tiene **su propio par de llaves ECDSA P-256** para firmar comandos y scripts. La llave privada se guarda cifrada en el servidor; la pública se entrega al agente al registrarse. El agente rechaza todo lo que no esté firmado con la llave de su empresa.
- **Planes y límites** (agentes, sucursales, usuarios, módulos) se validan en el servidor, no solo en la interfaz. Módulos: `monitoreo` (base), `remoto` (comandos, terminal, archivos, escritorio), `red` (red y data center), `reportes_avanzados`.
- El rol **Superadministrador** (proveedor del sistema) no pertenece a ninguna empresa. Acceso de soporte a una empresa: solo con permiso de la empresa, por tiempo limitado y auditado.

---

## 6. Base de datos

### 6.1 Script base

`database/01_schema.sql` ya existe y crea la base `MonitoreoTI` con 37 tablas, índices, la vista `vw_ubicacion_red_maquina`, el procedimiento `sp_resumir_y_purgar_metricas` y roles/permisos iniciales. **Léelo completo antes de empezar.** Convenciones que ya define y que debes respetar:

- Fechas en UTC con `DATETIME2(0)` y `SYSUTCDATETIME()`.
- Máquinas, dispositivos y usuarios **no se borran**: se marcan con estado `baja` o `activo = 0`.
- Solo las tablas de detalle se borran en cascada.
- Valores de estado controlados con `CHECK`.

Tablas existentes por área:

- **Seguridad:** `roles`, `permisos`, `rol_permiso`, `usuarios`.
- **Organización e inventario:** `sucursales`, `usuario_sucursal`, `departamentos`, `maquinas`, `componentes`, `software_instalado`, `interfaces_red`, `agentes`, `tokens_registro`.
- **Red y data center:** `salas`, `gabinetes`, `modelos_dispositivo`, `dispositivos_red`, `puertos`, `tabla_mac`, `enlaces_red`, `metricas_puerto`, `puntos_red`, `cableado`.
- **Monitoreo y alertas:** `metricas`, `metricas_hora`, `eventos_error`, `procesos_snapshot`, `reglas_alerta`, `alertas`.
- **Soporte y gestión remota:** `tickets`, `ticket_comentarios`, `ticket_adjuntos`, `mantenimientos`, `scripts`, `comandos`, `sesiones_remotas`, `auditoria`.

### 6.2 Cambios multi-tenant (crear `02_multitenant.sql`)

Como la base aún no tiene datos reales, puedes **modificar directamente `01_schema.sql`** en vez de crear migraciones de alteración; pregúntame cuál prefiero en la Fase 0. Cambios:

1. Tablas nuevas:
   - `empresas` (id, nombre, rnc, correo_contacto, telefono, plan_id, estado [`activa`,`suspendida`,`cancelada`], creado).
   - `planes` (id, nombre, max_agentes, max_sucursales, max_usuarios, modulos [JSON o tabla puente], precio_mensual, activo).
   - `suscripciones` (id, empresa_id, plan_id, inicio, fin, estado).
   - `usuario_empresa` (usuario_id, empresa_id, rol_id) — un usuario puede estar en varias empresas con un rol en cada una.
   - `empresa_llaves` (empresa_id, llave_publica, llave_privada_cifrada, creada, activa) — llaves de firma por empresa.
   - `accesos_soporte` (id, empresa_id, superadmin_id, autorizado_por, desde, hasta, motivo).
2. Agregar `empresa_id INT NOT NULL REFERENCES empresas(id)` a **todas** las tablas de negocio, incluidas las de alto volumen (`metricas`, `eventos_error`, `tabla_mac`, `metricas_puerto`, `auditoria`, etc.) para que RLS e índices no dependan de joins.
3. Tablas globales sin `empresa_id`: `permisos`, `planes`. `roles` y `modelos_dispositivo` aceptan `empresa_id NULL` = global (roles del sistema y catálogo base), o un valor = propio de esa empresa.
4. Convertir los `UNIQUE` de negocio en únicos por empresa: ej. `sucursales.nombre` → `(empresa_id, nombre)`, `dispositivos_red.nombre`, `scripts.nombre`, `departamentos.nombre`, `interfaces_red.mac` → `(empresa_id, mac)`.
5. `usuarios.correo` sigue siendo único global; `usuarios.rol_id` se mueve a `usuario_empresa.rol_id`. Agregar `usuarios.es_superadmin BIT`.
6. Todos los índices de las tablas de negocio empiezan por `empresa_id`.
7. Crear el esquema `seguridad`, la función de predicado y la política `seguridad.politica_empresa` (FILTER y BLOCK predicates) sobre cada tabla con `empresa_id`.
8. Ajustar la vista `vw_ubicacion_red_maquina` y el procedimiento `sp_resumir_y_purgar_metricas` para incluir `empresa_id`.

---

## 7. Servidor (ASP.NET Core)

### 7.1 Autenticación y autorización

- Login con correo + contraseña (hash Argon2id o BCrypt) + **2FA TOTP obligatorio** para roles con acceso remoto.
- JWT de acceso (15 min) + refresh token rotativo (guardado como hash en BD, revocable).
- Autorización por **permisos** (claves de la tabla `permisos`, ej. `remoto.conectar`) con políticas de ASP.NET; los roles solo agrupan permisos.
- El técnico solo ve las sucursales asignadas en `usuario_sucursal` (excepto Administrador).
- Rate limiting en login y endpoints sensibles; bloqueo temporal tras intentos fallidos.

### 7.2 API REST (prefijo `/api/v1`)

Grupos de endpoints (CRUD + acciones). Paginación, filtros y orden en todas las listas.

- `auth`: login, verify-2fa, refresh, logout, cambiar empresa, perfil, activar 2FA.
- `dashboard`: resumen general, resumen por sucursal, top equipos con problemas.
- `sucursales`, `departamentos`.
- `maquinas`: lista, detalle, componentes, software, procesos, servicios, eventos, métricas (rango de fechas), historial, QR (PNG y PDF en lote), dar de baja, buscar por `codigo_qr`.
- `instaladores`: crear token de registro, listar, revocar, descargar .msi configurado, aprobar equipos pendientes.
- `alertas` y `reglas-alerta`.
- `tickets`, comentarios, adjuntos (subida de imágenes), `mantenimientos`.
- `scripts`, `comandos` (ejecutar en uno o varios equipos, ver resultado).
- `sesiones-remotas`: solicitar sesión (devuelve URL del relay y token de un solo uso), historial.
- `red`: salas, gabinetes, modelos, dispositivos, puertos, tabla MAC, enlaces, puntos de red, cableado, diferencias detectadas, buscar MAC/IP, ruta completa de un equipo, importación CSV/Excel.
- `usuarios`, `roles`, `permisos`, `auditoria`, `configuracion`.
- `admin` (solo superadministrador): empresas, planes, suscripciones, uso, accesos de soporte.

### 7.3 SignalR (`/hubs/tiempo-real`)

Grupos por empresa y por sucursal. Eventos al cliente: `maquinaEstadoCambio`, `metricasNuevas` (solo a quien tenga abierta esa ficha), `alertaNueva`, `alertaActualizada`, `ticketActualizado`, `puertoEstadoCambio`, `comandoResultado`.

### 7.4 Gateway de agentes (`/agent/ws`)

- WebSocket por agente, autenticado con su credencial permanente (`agentes.token_hash`).
- Mantiene un registro en memoria de agentes conectados (`ConcurrentDictionary`), con su `empresa_id`, `maquina_id` y si es recolector.
- Procesa los mensajes de la sección 9 y los persiste por lotes (no una escritura por mensaje).
- Endpoint de registro `/agent/register` (HTTPS): recibe token de registro + datos del equipo; valida vencimiento y usos; crea o reutiliza la máquina; emite credencial permanente y la llave pública de firma de la empresa.

### 7.5 Servicios en segundo plano

- **Motor de alertas:** evalúa reglas sobre métricas, eventos, servicios, estado de agentes y puertos; aplica `duracion_min`; evita duplicar alertas abiertas; resuelve automáticamente cuando la condición desaparece; dispara notificaciones (SignalR, correo, push).
- **Detector de desconexión:** marca máquinas `desconectado` si el agente no envía heartbeat en 3 minutos.
- **Correlación MAC** (sección 11.3).
- **Resumen y limpieza de métricas:** ejecuta `sp_resumir_y_purgar_metricas` cada hora (o lo deja al Agente de SQL Server; configurable).
- **Expiración:** tokens de registro, sesiones remotas y comandos vencidos.

### 7.6 Auditoría

Middleware/servicio que registra en `auditoria` toda acción de escritura, login, cambio de permisos, comando, sesión remota y acceso de soporte, con usuario, empresa, IP y valores antes/después en JSON.

---

## 8. Agente (C# .NET Worker Service)

### 8.1 Módulos

| Módulo | Responsabilidad |
| --- | --- |
| Servicio principal | Corre como servicio de Windows (`LocalSystem`), arranca con el equipo |
| Conexión | WebSocket WSS saliente al servidor, reconexión con espera exponencial, heartbeat cada 30 s |
| Recolector local | Métricas, inventario, eventos, procesos, servicios, interfaces de red |
| Ejecutor | Ejecuta comandos y scripts **solo si la firma ECDSA es válida** para la llave de su empresa; timeout; devuelve salida |
| Recolector SNMP | Solo si el servidor lo marca como recolector: consulta switches, router e impresoras de su sucursal |
| Actualizador | Descarga nueva versión, verifica firma y hash, se reemplaza y reinicia el servicio |
| Cola local | SQLite: guarda mensajes si no hay conexión y los envía al reconectar |
| Lanzador de sesión | Inicia `SessionHelper` en la sesión del usuario activo (`WTSQueryUserToken` + `CreateProcessAsUser`) y se comunica por named pipe |
| Registro | Primera ejecución: usa el token de registro incrustado en la instalación y guarda la credencial cifrada con DPAPI (ámbito máquina) |

### 8.2 Qué recolecta y cada cuánto

| Dato | Fuente en Windows | Frecuencia |
| --- | --- | --- |
| CPU, RAM, disco (%), temperatura | `PerformanceCounter`/WMI `Win32_PerfFormattedData_*`, `Win32_OperatingSystem`, `Win32_LogicalDisk`, `MSAcpi_ThermalZoneTemperature` (si existe) | cada 60 s (configurable) |
| Salud del disco (SMART) | WMI `root\wmi` `MSStorageDriver_FailurePredictStatus` y `MSStorageDriver_FailurePredictData` | cada 6 h |
| Hardware | `Win32_Processor`, `Win32_PhysicalMemory`, `Win32_DiskDrive`, `Win32_VideoController`, `Win32_BaseBoard`, `Win32_BIOS`, `Win32_Battery` | al iniciar y cada 24 h |
| Sistema operativo | `Win32_OperatingSystem`, actualizaciones pendientes, uptime | cada 6 h |
| Software instalado | Registro: `HKLM\...\Uninstall` (64 y 32 bits). **No usar `Win32_Product`** | cada 24 h |
| Errores del sistema | `EventLogReader` en logs System y Application, niveles Crítico y Error | cada 60 s (solo nuevos) |
| Procesos | `System.Diagnostics.Process` (top 20 por CPU y RAM) | cada 5 min |
| Servicios | `ServiceController` (detectar servicios automáticos detenidos) | cada 5 min |
| Interfaces de red | `NetworkInterface` + `Win32_NetworkAdapterConfiguration`: nombre, MAC, IP, tipo, velocidad, gateway | al iniciar y al cambiar |
| Antivirus y firewall | WMI `root\SecurityCenter2` `AntiVirusProduct`, estado del firewall | cada 6 h |

Traducción de eventos comunes a lenguaje simple (tabla en el servidor, ampliable): ej. Kernel-Power 41 → "El equipo se apagó de forma inesperada"; disk 7/51/153 → "Errores de lectura en el disco"; WHEA → "Error de hardware detectado".

### 8.3 Instalación

- `.msi` con WiX que instala el servicio, guarda la URL del servidor y el token de registro de la sucursal (propiedades del MSI), y lo arranca.
- El servidor genera el `.msi` ya configurado por sucursal, o un comando de instalación silenciosa: `msiexec /i agente.msi /quiet SERVER_URL=... ENROLL_TOKEN=...`.
- Desinstalación limpia; el agente no se puede detener sin permisos de administrador.
- El ejecutable y el `.msi` se firman con el certificado de firma de código (configurable en el pipeline de compilación).

---

## 9. Protocolo agente ↔ servidor

Sobre el WebSocket, mensajes JSON con este sobre:

```json
{ "v": 1, "tipo": "metricas", "id": "uuid", "ts": "2026-10-26T19:40:00Z", "datos": { } }
```

Del agente al servidor: `hola` (versión, hostname, SO, es_recolector), `heartbeat`, `metricas`, `inventario_hardware`, `inventario_software`, `interfaces_red`, `eventos`, `procesos`, `servicios`, `seguridad`, `resultado_comando`, `snmp_resultado` (solo recolector), `sesion_lista`, `error_agente`.

Del servidor al agente: `bienvenida` (configuración: frecuencias, si es recolector, lista de dispositivos SNMP a consultar con credenciales cifradas para ese agente), `ejecutar_comando` (`comando_id`, `tipo`, `contenido`, `firma`, `timeout_s`), `abrir_sesion_remota` (`sesion_id`, `tipo`, `url_relay`, `token_un_uso`), `cerrar_sesion_remota`, `actualizar_agente` (`version`, `url`, `sha256`, `firma`), `solicitar_inventario`, `config_actualizada`.

Reglas: el servidor confirma (`ack`) los mensajes con `id`; el agente reintenta lo no confirmado desde la cola local; mensajes grandes se comprimen (gzip); versión del protocolo en `v` para compatibilidad.

---

## 10. Acceso remoto propio

### 10.1 Flujo de sesión

1. El técnico pulsa "Conectar", "Terminal" o "Archivos" en el panel (o "Conectar" en la app).
2. El servidor valida permiso, 2FA reciente, plan con módulo `remoto` y que usuario y máquina sean de la misma empresa.
3. Crea `sesiones_remotas` y un token de un solo uso (válido 60 s); envía `abrir_sesion_remota` al agente.
4. Agente y panel se conectan a `/relay/{sesion_id}` con su token. El relay solo reenvía bytes entre ambos.
5. Al cerrar, se registra duración en `sesiones_remotas` y en auditoría.
6. En el equipo remoto se muestra un aviso visible mientras haya una sesión activa. Configurable por empresa: pedir permiso al usuario antes de conectar.

### 10.2 Implementación por etapas

| Etapa | Agente | Panel (Electron) |
| --- | --- | --- |
| Terminal | ConPTY (`CreatePseudoConsole`) con PowerShell o CMD | xterm.js en ventana propia |
| Archivos | Listar, descargar y subir por partes (chunks de 256 KB), con permisos del sistema | Explorador con barra de progreso; guardar en disco local |
| Ver pantalla V1 | `SessionHelper` captura con GDI (`BitBlt`), divide en mosaicos, envía solo los que cambian en JPEG | Dibujo en `<canvas>` |
| Control | `SendInput` para mouse y teclado desde `SessionHelper` | Captura de eventos de la ventana, escalado de coordenadas |
| Ver pantalla V2 | Desktop Duplication API (DXGI) + compresión H.264 por Media Foundation | Decodificación con WebCodecs |
| Pantalla de inicio / UAC | `SessionHelper` en el escritorio seguro (Winlogon) | — |

Formato de frames de pantalla: binario, con encabezado (tipo, x, y, ancho, alto, longitud) seguido de los bytes JPEG. Mensajes de entrada: JSON compacto o binario (tipo, x, y, botón, tecla).

---

## 11. Red y data center

### 11.1 Recolección SNMP (agente recolector)

SNMPv3 de solo lectura (authPriv con SHA y AES). Cada 5 min (configurable) por dispositivo:

| Dato | OID |
| --- | --- |
| Descripción, nombre, uptime | `sysDescr` 1.3.6.1.2.1.1.1, `sysName` 1.3.6.1.2.1.1.5, `sysUpTime` 1.3.6.1.2.1.1.3 |
| Nombre del puerto y alias | `ifName` 1.3.6.1.2.1.31.1.1.1.1, `ifAlias` 1.3.6.1.2.1.31.1.1.1.18 |
| Estado admin y operativo | `ifAdminStatus` 1.3.6.1.2.1.2.2.1.7, `ifOperStatus` 1.3.6.1.2.1.2.2.1.8 |
| Velocidad | `ifHighSpeed` 1.3.6.1.2.1.31.1.1.1.15 |
| Tráfico | `ifHCInOctets` 1.3.6.1.2.1.31.1.1.1.6, `ifHCOutOctets` 1.3.6.1.2.1.31.1.1.1.10 |
| Errores y descartes | `ifInErrors` 1.3.6.1.2.1.2.2.1.14, `ifOutErrors` 1.3.6.1.2.1.2.2.1.20, `ifInDiscards` 1.3.6.1.2.1.2.2.1.13 |
| MAC por puerto | `dot1qTpFdbPort` 1.3.6.1.2.1.17.7.1.2.2.1.2 (si no existe, `dot1dTpFdbPort` 1.3.6.1.2.1.17.4.3.1.2) y `dot1dBasePortIfIndex` 1.3.6.1.2.1.17.1.4.1.2 para mapear a ifIndex |
| IP de cada MAC (router o switch capa 3) | `ipNetToMediaPhysAddress` 1.3.6.1.2.1.4.22.1.2 |
| Vecinos LLDP | `lldpRemSysName` 1.0.8802.1.1.2.1.4.1.1.9, `lldpRemPortId` 1.0.8802.1.1.2.1.4.1.1.7 |
| Consumibles de impresora | `prtMarkerSuppliesLevel` 1.3.6.1.2.1.43.11.1.1.9 y `prtMarkerSuppliesMaxCapacity` 1.3.6.1.2.1.43.11.1.1.8 |

El fabricante de una MAC se obtiene del prefijo OUI (tabla OUI cargada en el servidor).

### 11.2 Administración del data center

- **Catálogo de modelos:** al crear un dispositivo de un modelo, se crean sus puertos automáticamente según `cantidad_puertos` y `tipo_puertos`.
- **Gabinetes:** validar que un dispositivo no ocupe U ya ocupadas ni exceda la altura del gabinete (validación en servidor).
- **Patch panels:** dispositivos pasivos (`es_activo = 0`), sin SNMP; su cableado se documenta en `cableado`.
- **Cableado:** un cable une dos puertos o un puerto con una roseta (`puntos_red`). Con eso se calcula la ruta completa PC → roseta → patch panel → switch → uplink → core.
- **Importación masiva** desde CSV/Excel con vista previa y validación antes de guardar.

### 11.3 Correlación automática PC ↔ puerto

1. Marcar como **troncal** (`puertos.es_troncal = 1`) todo puerto donde LLDP detecta otro dispositivo de red, o que tenga más de N MAC (configurable, por defecto 10) y no esté marcado como `es_host_vm`. Un técnico puede fijarlo manualmente (`asignacion_manual`).
2. Para cada MAC en un puerto **de acceso**, buscar en `interfaces_red.mac` (misma empresa). Si coincide, llenar `tabla_mac.maquina_id`.
3. Actualizar `ultima_vez`; si la MAC aparece en otro puerto de acceso, registrar evento "equipo movido".
4. MAC sin coincidencia → aparece en "Equipos desconocidos" con fabricante e IP.
5. **Validación cruzada:** si la ubicación detectada por SNMP no coincide con el cableado documentado, crear registro en "Diferencias detectadas".
6. Casos especiales: teléfono IP con PC detrás (2 MAC, normal); host de VMs (muchas MAC, no troncal si `es_host_vm`); Wi-Fi (MAC vista en el puerto del AP: mostrar "vía AP"); MAC aleatorias de celulares (agrupar, no alertar).

---

## 12. Panel de administración (Electron + React)

### 12.1 Seguridad de Electron (obligatorio)

- `contextIsolation: true`, `nodeIntegration: false`, `sandbox: true` en todas las ventanas.
- Preload con `contextBridge` exponiendo solo funciones necesarias (abrir ventana, guardar archivo, notificar, leer/guardar token); validar cada mensaje IPC.
- CSP estricta: solo conexiones al dominio configurado del servidor; no cargar contenido remoto ni `eval`.
- Token de sesión guardado con `safeStorage`.
- Bloquear navegación a URLs externas (abrir en navegador del sistema solo enlaces permitidos).

### 12.2 Proceso principal

Ventana principal (1440×900 mínimo 1280×720, barra de título propia), ícono en bandeja con menú, notificaciones nativas de Windows para alertas críticas, ventanas separadas para escritorio remoto, terminal y vista 3D, auto-actualización desde nuestro servidor, enlaces profundos `monitoreoti://maquina/{id}`.

### 12.3 Pantallas

Navegación lateral: Dashboard, Equipos, Sucursales, Alertas, Tickets, Acceso remoto, Red (Resumen, Data center, Dispositivos, Mapa de red, Buscar MAC/IP, Equipos desconocidos, Diferencias), Reportes, Instaladores, Usuarios, Configuración, Auditoría. Barra superior: buscador global (nombre, QR, IP, MAC), selector de empresa (si aplica), notificaciones, perfil.

Pantallas (numeración del diseño):

- **Acceso:** W-01 Login, W-02 Verificación 2FA, W-03 Recuperar contraseña.
- **Monitoreo:** W-04 Dashboard, W-05 Lista de equipos, W-06 a W-12 Ficha del equipo (Resumen, Componentes, Errores, Procesos y servicios, Software, Historial, Información y QR; la ficha también muestra "Conexión de red": switch, puerto, VLAN), W-13 Agregar/editar equipo manual, W-14 Imprimir QR en lote.
- **Sucursales e instalación:** W-15 a W-17 Sucursales (lista, detalle, formulario), W-18 Instaladores, W-19 Equipos pendientes de aprobación.
- **Alertas y tickets:** W-20 Alertas, W-21 Detalle de alerta, W-22/W-23 Reglas, W-24 Tickets Kanban, W-25 Tickets lista, W-26 Detalle de ticket, W-27 Calendario de mantenimiento.
- **Acceso remoto:** W-28 Escritorio remoto, W-29 Conectando/error, W-30 Terminal, W-31 Explorador de archivos, W-32 Ejecutar script, W-33 Biblioteca de scripts, W-34 Historial de sesiones.
- **Administración:** W-35 Reportes, W-36/W-37 Usuarios, W-38 Roles y permisos, W-39 Auditoría, W-40 Configuración, W-41 Mi perfil.
- **Red y data center:** W-42 Resumen de red, W-43 Vista 3D del data center, W-44 Gabinete 3D, W-45 Gabinete 2D, W-46 Detalle de switch, W-47 Panel de puerto, W-48 Dispositivos, W-49 Agregar/editar dispositivo, W-50 Agregar/editar gabinete, W-51 Mapa de topología, W-52 Buscar MAC/IP, W-53 Equipos desconocidos, W-54 Alertas de red, W-56/W-57 Catálogo de modelos, W-58 Salas, W-59 Plano de la sala, W-60 Editor de gabinete (arrastrar y soltar), W-61 Asistente agregar dispositivo, W-62 Detalle de patch panel, W-63 Editor de cableado, W-64 Puntos de red, W-65 Ruta completa, W-66 Diferencias detectadas, W-67 Asignar puerto manual, W-68 Importación masiva, W-69 Historial del data center.
- **Portal del proveedor (solo superadministrador):** empresas, planes, suscripciones, uso por empresa, accesos de soporte.

### 12.4 Diseño visual

- Modo claro y oscuro. Color primario azul. Colores de estado solo para estado: verde `sano`, amarillo `advertencia`, rojo `critico`, gris `desconectado`.
- Colores de puerto: verde conectado, gris libre, amarillo con errores, rojo caído, azul troncal.
- Tipografía sans-serif para la interfaz; monoespaciada para métricas, logs y terminal.
- Componentes propios reutilizables en `desktop/src/components` (tabla, tarjeta, badge de estado, medidor circular, gráfica, modal, toast, estado vacío, skeleton). Constantes de colores y estados en `shared`.
- Si te entrego diseños de Claude Design, síguelos fielmente.

---

## 13. App móvil (React Native + Expo)

- Navegación inferior: Inicio, Escanear (botón central), Alertas, Tickets, Perfil.
- Login con 2FA; desbloqueo con huella o Face ID (`expo-local-authentication`); token en `expo-secure-store`.
- **Escáner QR** (`expo-camera`): el QR contiene una URL tipo `monitoreoti://q/{codigo_qr}`; reconoce máquinas, gabinetes, dispositivos de red y rosetas, y abre la pantalla correspondiente. Opción de escribir el código a mano.
- Pantallas: M-01 a M-28 (acceso, inicio, escáner, ficha del equipo con semáforo y medidores, componentes, errores, procesos, historial, equipo desconectado, buscar, alertas, tickets con fotos, vista remota con zoom, acciones rápidas con confirmación, notificaciones, perfil, sin conexión, estados vacíos, carga) y M-29 a M-39 (red: resumen, gabinete escaneado, switch escaneado, detalle de puerto, buscar MAC/IP, gabinete 3D simple, roseta escaneada, registrar cable en sitio, agregar dispositivo rápido, corregir ubicación).
- Notificaciones push (Expo/FCM/APNs) para alertas críticas asignadas.
- Modo sin conexión: guarda el último estado consultado de cada equipo.

---

## 14. Reglas de alerta por defecto

| Regla | Advertencia | Crítico |
| --- | --- | --- |
| Disco usado | > 80 % | > 95 % |
| SMART | sectores reasignados en aumento | estado "fallando" |
| CPU sostenida | > 85 % por 15 min | > 95 % por 30 min |
| RAM | > 85 % | > 95 % |
| Temperatura CPU | > 80 °C | > 90 °C |
| Sin reportar | 10 min | 30 min |
| Errores críticos del sistema | 1 en 24 h | 3+ en 24 h |
| Servicio automático detenido | — | inmediato |
| Tóner | < 20 % | < 5 % |
| Antivirus desactivado | — | inmediato |
| Errores en puerto | > 100/h | > 1000/h |
| Puerto con equipo conocido caído | — | inmediato |
| Switch sin responder SNMP | 10 min | 30 min |

Se crean al crear cada empresa y cada empresa puede ajustarlas.

---

## 15. Seguridad (lista de verificación)

- TLS en todo; HSTS en el servidor.
- Contraseñas con Argon2id/BCrypt; 2FA TOTP; bloqueo por intentos.
- Credenciales SNMP y secretos 2FA cifrados en BD (AES-GCM con llave fuera de la BD, rotable).
- Comandos y scripts firmados por empresa; el agente verifica antes de ejecutar.
- Actualizaciones del agente y del panel firmadas y verificadas por hash.
- Tokens de sesión remota de un solo uso y corta duración.
- Aislamiento multi-tenant en las cuatro capas (sección 5) con pruebas automáticas que intenten acceder a datos de otra empresa y deban fallar.
- Validación de entrada en servidor (FluentValidation) y en clientes (zod).
- Auditoría de toda acción sensible.
- Nunca registrar en logs contraseñas, tokens, credenciales SNMP ni contenido de archivos remotos.

---

## 16. Calidad y pruebas

- Servidor: xUnit para Domain y Application; pruebas de integración de la API contra SQL Server (LocalDB o contenedor) incluyendo **pruebas de aislamiento entre empresas**.
- Agente: pruebas de parsers (eventos, SNMP, protocolo), verificación de firmas y cola local.
- Escritorio y móvil: Vitest + Testing Library para componentes y lógica; Playwright para flujos principales del panel.
- Formato y estilo: `dotnet format`, ESLint + Prettier.
- README por carpeta con cómo compilar, configurar y ejecutar en desarrollo.
- Datos de prueba en `database/03_seed_dev.sql`: 2 empresas, 5 sucursales (Santo Domingo, Santiago, La Romana, Puerto Plata, San Pedro de Macorís), equipos CONTA-03, CAJA-01, RRHH-02, VENTAS-05, SERVIDOR-01, IMP-RECEPCION; gabinetes GAB-A01, GAB-A02; switches SW-CORE-01, SW-ACC-02, SW-ACC-03.

---

## 17. Fases y criterios de aceptación

**Fase 0 — Base del proyecto**
- Monorepo con la estructura de la sección 4.2; solución .NET; proyectos Electron, Expo y `shared` inicializados.
- Script de BD con multi-tenant (sección 6.2), RLS y datos de prueba.
- Entidades EF Core mapeadas a las tablas existentes (database-first; no generar tablas distintas a las del script).
- ✅ La base se crea desde cero con los scripts; la API arranca y responde `/health`; el panel y la app abren una pantalla vacía.

**Fase 1 — Agente básico y gateway**
- Registro con token de sucursal, conexión WSS, heartbeat, métricas, inventario de hardware y software, interfaces de red, cola local.
- Gateway que persiste por lotes y detector de desconexión.
- ✅ Un agente instalado en una PC aparece en la BD, envía métricas cada minuto y pasa a "desconectado" al apagarlo.

**Fase 2 — API, autenticación y panel base**
- Login con 2FA, permisos, multi-tenant activo, auditoría.
- Panel: W-01 a W-19 (dashboard, equipos, ficha, sucursales, instaladores, QR) con datos en vivo por SignalR.
- ✅ Dos empresas de prueba no pueden ver datos entre sí (prueba automática); se genera e instala un agente desde el panel.

**Fase 3 — Errores, procesos y alertas**
- Eventos, procesos, servicios, SMART, antivirus; motor de alertas con reglas por defecto; notificaciones; SNMP de impresoras.
- Panel: W-20 a W-27.
- ✅ Llenar un disco o detener un servicio genera la alerta correcta y se resuelve sola al corregirlo.

**Fase 4 — Red y data center**
- Recolector SNMP, catálogo, salas, gabinetes, dispositivos, puertos, correlación MAC, cableado, diferencias, importación.
- Panel: W-42 a W-69 (primero 2D, luego 3D).
- ✅ Una PC con agente aparece automáticamente en su switch y puerto correctos; desconectar el cable cambia el puerto a rojo.

**Fase 5 — App móvil**
- M-01 a M-39, escáner QR, push, modo sin conexión.
- ✅ Escanear la etiqueta de un equipo o switch muestra su estado real en menos de 2 segundos.

**Fase 6 — Gestión remota**
- Ejecutor de comandos y scripts firmados, terminal (ConPTY + xterm.js), explorador de archivos, tareas programadas, actualización automática del agente.
- Panel: W-30 a W-34.
- ✅ Un script sin firma válida es rechazado por el agente; uno firmado se ejecuta y devuelve su salida.

**Fase 7 — Escritorio remoto**
- SessionHelper, captura V1 (GDI + JPEG por mosaicos), relay, visor en ventana Electron, control de mouse y teclado; luego V2 (DXGI + H.264).
- Panel: W-28, W-29; app: M-21.
- ✅ Controlar una PC de otra sucursal desde el panel con latencia usable en una conexión normal.

**Fase 8 — Comercial multi-tenant**
- Portal del superadministrador, planes, suscripciones, límites por plan, accesos de soporte auditados.
- ✅ Una empresa en plan Básico no puede abrir sesiones remotas aunque llame a la API directamente.

---

## 18. Primer paso

Lee todo este documento y `database/01_schema.sql`. Luego preséntame el plan de la **Fase 0**: estructura de carpetas, cómo vas a aplicar los cambios multi-tenant a la base de datos (modificar el script o crear uno nuevo), cómo mapearás EF Core a las tablas existentes, y cualquier duda que tengas. No escribas código hasta que apruebe el plan.

---

## 19. Decisiones tomadas (registro)

Estas decisiones, aprobadas durante el desarrollo, **prevalecen sobre el texto anterior** donde lo contradicen.

**Fase 0 (2026-10-26)**

1. **Agente en Electron** (reemplaza C# .NET Worker Service, secciones 4.1, 4.2 y 8). Un solo paquete con dos modos:
   - *Modo servicio*: `electron.exe` con `ELECTRON_RUN_AS_NODE=1` registrado como servicio de Windows (LocalSystem), sin ventana y activo antes del inicio de sesión. Solo usa APIs de Node.
   - *Modo UI*: Electron en la sesión del usuario; cumple el papel de `SessionHelper` (pantalla, mouse/teclado, aviso de sesión, bandeja).
   - El protocolo (sección 9) se define en TypeScript en `shared/src/protocol.ts`; el servidor tiene su contraparte en C#. Ya no existe `MonitoreoTI.Agent.Shared`.
   - Las librerías nativas que requiera el agente (ConPTY, SNMP, SQLite, inyección de entrada) se consultarán antes de agregarlas.
2. **Scripts de base de datos**: `00_create_database.sql` (crea la base), `01_schema.sql` modificado directamente con el esquema multiempresa, `02_multitenant.sql` solo con Row-Level Security, `03_seed_dev.sql`. Los cambios de esquema futuros se hacen en `01` mientras no haya datos reales.
3. **FK compuestas por empresa** `(empresa_id, x_id)` en las relaciones entre tablas de negocio (excepto las `ON DELETE SET NULL`).
4. **`auditoria.empresa_id` acepta NULL** (acciones del superadministrador y del sistema fuera de una empresa).
5. **Tablas agregadas**: `refresh_tokens`, `plan_modulos` (módulos del plan como tabla puente); columnas `usuarios.intentos_fallidos` y `usuarios.bloqueado_hasta`. Las tablas de fases posteriores se agregan en su fase.
6. **Trabajos en segundo plano por empresa**: recorren las empresas con un ámbito y contexto de sesión por cada una; no existe un indicador que salte RLS. `sp_resumir_y_purgar_metricas` recibe `@empresa_id` y exige que coincida con el contexto.
7. **Excepciones de RLS**: `usuario_empresa` y `refresh_tokens` no tienen política (se leen durante el login, antes de conocer la empresa); solo las usa el servicio de autenticación.
8. **Login de aplicación** `monitoreoti_app` sin permisos de esquema; la API nunca usa `sa`.
9. **Herramientas**: `electron-vite` autorizado para compilar el panel y el agente. TypeScript 6.0 en todo el monorepo. La app móvil usa React 19 (exigido por Expo SDK 58); el panel se mantiene en React 18.

**Diseño del panel (2026-10-27)**

10. **Diseños de Claude Design entregados** (W-04 Dashboard general, W-05 Lista de equipos, W-06 Ficha del equipo · Resumen). Marca **Centinela**. Prevalecen sobre §12.4 donde difieren:
    - Color de marca: verde azulado oscuro (barra lateral ~`#0b1f26`, acento `#0f766e`), no azul. Fondo crema cálido; tarjetas blancas con borde suave y radios grandes.
    - Estados: sano verde, advertencia ámbar, crítico rojo, desconectado gris (se conserva). Acción destacada "Crear ticket" en naranja.
    - Tipografía: sans-serif para la interfaz; monoespaciada para etiquetas en mayúsculas, IP, nombres de equipo, métricas y fechas relativas.
    - Estructura: barra lateral oscura con logo, navegación con insignias de conteo y tarjeta de empresa al pie; barra superior con buscador global (⌘K), campana y perfil; encabezado de página con título, subtítulo y acciones.
    - Dashboard: 5 tarjetas KPI, barras apiladas de salud de la flota 7 días, lista de sucursales con barra de estados, alertas críticas recientes, top 5 equipos con más problemas.
    - Lista de equipos: filtros (sucursal, tipo, estado, SO), chips de resumen, tabla con selección múltiple, mini barras CPU/RAM/disco y barra de acciones en lote (ejecutar script, imprimir QR, reiniciar).
    - Ficha: migas de pan, cabecera con estado y acciones (Conectar, Terminal, Archivos, Crear ticket), pestañas, tarjetas CPU/RAM/disco/temperatura en vivo, gráfica 24 h con umbral, "Problemas detectados" con "Qué hacer", procesos con más consumo y salud de componentes.

**Fase 1 (2026-10-27)**

11. **Tokens del agente** (registro y credencial permanente): formato `{empresaId}.{secreto}`, secreto aleatorio de 256 bits. En la base solo se guarda el SHA-256 en hex (no Argon2: son secretos de alta entropía y hay que buscarlos por hash). El prefijo permite fijar el contexto de RLS antes de buscar el token; un secreto con otro prefijo no se encuentra. Reinstalar rota la credencial.
12. **Registro** (`POST /agent/register`): un solo `UPDATE` atómico valida vencimiento y cupo y consume el uso. Inexistente, vencido o agotado responden igual (401). Límite por IP (20/min). Reutiliza la máquina si la MAC coincide con la principal o con cualquiera de sus tarjetas; si estaba de baja, la reactiva. Se audita `agente.registro` / `agente.reinstalacion`.
13. **Agente sin librerías nativas**: WebSocket nativo de Node (permite la cabecera `Authorization`), cola local con `node:sqlite`, WMI/registro/DPAPI vía PowerShell 5.1 con ruta absoluta. Credencial cifrada con DPAPI de ámbito máquina en `%ProgramData%\MonitoreoTI\Agent`; el instalador (Fase 2) debe limitar esa carpeta a SYSTEM y administradores. Se eliminó el agente C# anterior; el `.msi` pasa a la Fase 2 (se genera desde el panel).
14. **MAC principal**: la del adaptador físico de la ruta por defecto (`Get-NetAdapter -Physical`); se descartan MAC administradas localmente y prefijos de máquinas virtuales.
15. **Gateway**: autenticación por `Authorization: Bearer` en el upgrade; el primer mensaje debe ser `hola` (10 s); cierre por inactividad a los 90 s; una conexión nueva del mismo agente reemplaza a la anterior. Se confirma (`ack`) todo mensaje válido; uno mal formado se confirma y se descarta (no bloquea la cola del agente); un fallo del servidor no se confirma y el agente lo reenvía.
16. **Persistencia por lotes**: heartbeats solo en memoria, volcados cada 30 s junto con el detector de desconexión (3 min sin contacto, solo máquinas con agente). Métricas por `SqlBulkCopy` cada 5 s; el ack sale al encolar (ante una caída del servidor se pierden como mucho segundos de métricas). Se deduplican por máquina y segundo. La hora de la muestra es la del agente si está entre −7 días y +5 min; si no, la de recepción.
17. **Frecuencias**: métricas cada 60 s (el servidor puede cambiarla en `bienvenida`), hardware y software al iniciar y cada 24 h, interfaces al iniciar, cada 6 h y al detectar un cambio (revisión cada 5 min). El sistema operativo viaja en `hola` y en el inventario de hardware (no hay mensaje propio cada 6 h). Salud de componentes `desconocido` hasta SMART (Fase 3). Temperatura solo si el equipo expone zonas térmicas ACPI.
18. **Estructura**: `MonitoreoTI.Workers` referencia a `Infrastructure`. `FluentValidation` en `Application`. Las cargas útiles del protocolo están en `shared/src/protocol.ts` y sus contrapartes en `Application/Agents/TelemetryContracts.cs`.

**Fase 2 (2026-10-27)**

19. **Orden de la fase**: primero autenticación, permisos y datos reales en el panel; el instalador del agente al final de la fase. El instalador será un **`.exe`** (`electron-builder` + NSIS), no `.msi`, con un **anfitrión de servicio propio en C#** (.NET 8 autocontenido) que Windows controla y que lanza y vigila el agente Electron.
20. **Pantallas sin diseño de Claude Design** (W-01 a W-03 y W-07 a W-19): se hacen con el estilo Centinela. Si llegan diseños después, se ajustan.
21. **Login en pasos con "desafíos"**: contraseña → 2FA (o su alta) → empresa → sesión. Cada paso intermedio entrega un JWT de 5 minutos con otra audiencia (`…-desafio`): no sirve como token de acceso ni para saltarse pasos. El secreto 2FA pendiente viaja cifrado dentro del desafío y solo se guarda al confirmar el primer código.
22. **2FA obligatorio** para el superadministrador y para quien tenga, en alguna de sus empresas, `remoto.conectar`, `remoto.terminal`, `remoto.archivos` o `scripts.ejecutar`. Una vez activado, se pide siempre. TOTP de 6 dígitos y 30 s con ±1 intervalo; cada código sirve una sola vez. Secreto cifrado con AES-256-GCM (`Auth:EncryptionKey`, fuera de la base).
23. **Tokens**: acceso JWT HS256 de 15 min con `empresa_id`, `rol` y un claim `perm` por permiso. Refresh opaco de 7 días, guardado como SHA-256, rotativo; si reaparece un refresh ya usado se cierran todas las sesiones del usuario. Los permisos se recalculan en cada renovación. Las llaves van en user-secrets o en variables de entorno; el API no arranca sin ellas.
24. **Bloqueo**: 5 fallos (contraseña o código 2FA) bloquean la cuenta 15 min. Límite de 20 peticiones por minuto por IP en `/api/v1/auth`. Un correo inexistente responde igual que una contraseña mala.
25. **Recuperar contraseña**: tabla nueva `tokens_restablecimiento` (sin empresa). Enlace de un solo uso que vence en 30 min; al usarlo se cierran las sesiones abiertas. Sin SMTP todavía: en desarrollo los correos se guardan como archivos en `%TEMP%\monitoreoti-correos` (no en el log, porque llevan tokens). El panel acepta pegar el enlace del correo.
26. **Panel**: el token de acceso vive solo en memoria; el refresh se guarda cifrado con `safeStorage` por el proceso principal, que valida cada mensaje IPC. El cliente de `/api/v1/auth` está en `shared` para reutilizarlo en la app móvil. Paquetes nuevos: `Otp.NET` y `QRCoder` (lista §4.3) y `Microsoft.AspNetCore.Authentication.JwtBearer` (de Microsoft).
27. **CORS del API**: solo los orígenes de `Cors:PanelOrigins` (en desarrollo, `http://localhost:5173`), sin credenciales. Al empaquetar el panel, el renderer se servirá con un esquema propio (`app://panel`) para tener un origen fijo que permitir; no se permitirá el origen `null` de `file://`.
28. **Alcance por sucursal**: el rol `Administrador` ve todas las sucursales; los demás roles, si tienen sucursales en `usuario_sucursal`, solo esas; sin asignaciones, todas. Un recurso de otra empresa o de una sucursal no asignada responde 404, igual que uno inexistente.
29. **Historial del semáforo**: tabla nueva `estado_diario` (empresa, fecha, conteos por estado), con RLS. La escribe el servicio de presencia en cada ciclo; alimenta la gráfica de 7 días del dashboard. Con sucursales restringidas solo se muestra el día actual (no hay foto por sucursal).
30. **Tiempo real** (SignalR `/hubs/tiempo-real`): grupos por sucursal (`maquinaEstadoCambio`) y por máquina (`metricasNuevas`, solo con la ficha abierta; la suscripción valida el acceso). El panel reintenta la conexión inicial y se re-suscribe al reconectar. CORS permite además `X-Requested-With` y `X-SignalR-User-Agent`.
31. **Aprobación de equipos (W-19)**: columnas nuevas `tokens_registro.requiere_aprobacion`, `tokens_registro.revocado`, `tokens_registro.nombre` y `agentes.aprobado`. Un agente pendiente no puede conectarse; rechazarlo lo revoca y da de baja la máquina. El token en claro solo se muestra al crearlo.
32. **Escrituras** (equipos, sucursales, instaladores): validadas en el servidor, con referencias comprobadas dentro de la empresa y el alcance de sucursales, y auditadas con valores de antes y después. Dar de baja no borra: marca `baja` y revoca el agente.
33. **QR**: contenido `monitoreoti://q/{codigo_qr}`. Etiquetas en lote (W-14) como hoja imprimible desde el panel (también "Guardar como PDF" del diálogo de impresión); no se agregó librería de PDF.
34. **Instalador del agente**: `MonitoreoTI-Agente-Setup.exe` (electron-builder + NSIS, sin asar). Interactivo (pide servidor y token) o silencioso `/S /SERVER= /TOKEN=`. El anfitrión `MonitoreoTI.AgentHost.exe` (.NET 8 autocontenido, `Microsoft.Extensions.Hosting.WindowsServices`) crea el servicio `MonitoreoTIAgent` (LocalSystem, arranque automático, reinicio por fallo), limita `%ProgramData%\MonitoreoTI\Agent` a SYSTEM y administradores (por SID) y escribe `config.json`. El agente borra el token de `config.json` al obtener su credencial. Una actualización conserva credencial y cola; la desinstalación borra los datos. El servidor entrega el archivo configurado en `Instaladores:Archivo`. Sin certificado, SmartScreen muestra "editor desconocido". El modo UI (bandeja) no se inicia con la sesión todavía (Fase 7).
35. **Panel**: `@tanstack/react-query` y `@microsoft/signalr` (lista §4.3). Un 401 en cualquier consulta cierra la sesión; un fallo de red o del servidor (5xx) se reintenta sin cerrarla. Guardar archivos (instalador) pasa por el proceso principal con el diálogo del sistema (`file:save`), con nombre y tamaño validados.
36. **Salud del equipo (Fase 3)**: el agente envía `volumenes` (10 min), `procesos` (top 20 + total, 5 min), `servicios` (solo los de inicio automático, 5 min), `eventos` (System y Application, nivel crítico y error, cada 60 s desde un marcador local), `salud_discos` (Get-PhysicalDisk + contadores de confiabilidad + SMART 5/197, 6 h) y `seguridad` (SecurityCenter2 + firewall, 6 h). Tablas nuevas `volumenes` y `servicios`; las fotos de procesos se conservan 1 día. El inventario de hardware solo reemplaza componentes de hardware y conserva la salud SMART del disco; antivirus y firewall se guardan como componentes.
37. **Motor de alertas**: reglas puras (`HealthRules`) con los umbrales de §14 o los de `reglas_alerta` de la empresa (las de sucursal quedan para W-22). Corre cada minuto por empresa con su propio contexto (sin saltar RLS). Una alerta abierta por `codigo` (ej. `disco_lleno:C:`, `servicio:Spooler`); lo que deja de encontrarse se resuelve solo. Cada alerta guarda explicación, "qué hacer" y origen; los eventos de Windows conocidos se traducen con un catálogo (`EventCatalog`). El semáforo del equipo es el peor nivel abierto. Para evitar falsas alarmas: un servicio solo alerta si estaba en ejecución y se detuvo; CPU exige la duración de la regla (crítico, el doble); RAM, 5 min sostenidos; antivirus, solo si ninguno está activo; disco, CPU, RAM, temperatura y servicios solo con datos de menos de 10 min.
38. **Ficha (W-06)**: tarjetas con RAM "usado / total GB" y "Disco C:" desde los volúmenes; procesos con más consumo en el Resumen; servicios en "Procesos y servicios"; "Salud de componentes" incluye antivirus, firewall y red. "Crear ticket" está visible pero desactivado hasta que se hagan los tickets.
39. **Alertas y reglas (W-20 a W-23)**: las reglas están en una pestaña de la pantalla de Alertas. Atender pasa la alerta a "en proceso" a nombre del técnico; resolver la cierra. Ambas acciones admiten una nota y quedan en auditoría, y el historial del detalle se arma con esa auditoría (no hay tabla de historial). Si se resuelve a mano una alerta del motor y el problema sigue, el motor abre una nueva en la próxima revisión; el detalle muestra cuántas veces se repitió en 30 días. Reglas: la de una sucursal reemplaza a la de la empresa y esta a la de §14; quitar un ajuste vuelve al nivel anterior. Un usuario limitado a algunas sucursales solo ajusta las reglas de esas sucursales. Las métricas de impresoras y red se pueden ajustar desde ya y se evaluarán con su módulo. SignalR envía `alertaNueva` y `alertaActualizada` al grupo de la sucursal.
40. **Tickets (W-24 a W-26)**: tablero (abierto, en proceso, en espera, resuelto; se arrastra o se usa el selector "Mover", que también sirve con teclado), lista (incluye cerrados) y detalle con conversación, imágenes e historial (desde la auditoría). Ver, crear, comentar y adjuntar: `tickets.crear` o `tickets.gestionar` (la política acepta "cualquiera de" con `perm:a|b`); quien solo crea (Empleado) ve únicamente sus tickets y los edita mientras sigan abiertos; mover, asignar y cerrar: `tickets.gestionar`. Solo se asigna a usuarios cuyo rol gestiona tickets. Pasar a "en proceso" un ticket sin técnico lo asigna a quien lo mueve; asignarlo pasa un ticket abierto a "en proceso". Crear desde una alerta toma el equipo de la alerta, la pasa a "en proceso" y no permite un segundo ticket activo para la misma alerta. Imágenes: JPG, PNG o WebP de hasta 5 MB y 10 por ticket, enviadas en base64 (sirve igual para la app móvil); el servidor comprueba los primeros bytes del archivo contra el tipo declarado, les pone nombre y las guarda en `Adjuntos:Carpeta` (por defecto `App_Data/adjuntos` del API, fuera del repositorio). No hay URL pública: se piden con la sesión. SignalR: `ticketActualizado` al grupo de la sucursal.
41. **Fase 4 por etapas** (el usuario adelantó la Fase 4; quedan pendientes de la Fase 3 el calendario W-27 y las notificaciones, y las impresoras SNMP se hacen con el recolector de 4.2): 4.1 inventario del data center sin SNMP, 4.2 recolector SNMP, 4.3 correlación automática y alertas de red, 4.4 vistas 3D. **SNMP en el anfitrión C#** (`MonitoreoTI.AgentHost`, con `Lextm.SharpSnmpLib`, permitida en §4.3): el agente Node le pide cada consulta y recibe JSON; no se agrega una librería SNMP a Node. **Solo SNMPv3** authPriv (SHA/AES), como dice §11.1.
42. **Data center (4.1)**: `dispositivos_red.sucursal_id` (nueva, obligatoria): la del gabinete si va en uno; sin ella un AP o un equipo fuera de gabinete no tendría alcance por sucursal ni recolector. Al crear un dispositivo se crean sus puertos según `cantidad_puertos` y `tipo_puertos` ("48 RJ45 + 4 SFP+"; lo no descrito es RJ45; nombres provisionales que SNMP reemplazará). El catálogo base (empresa NULL) es de solo lectura; cada empresa agrega modelos propios. Gabinete: el servidor rechaza U ocupadas o fuera de la altura (con el nombre del equipo que estorba) y no deja achicar un gabinete por debajo de sus equipos. Cableado: un puerto de equipo activo admite un cable, uno de patch panel dos (roseta y switch), una roseta uno, y todo en la misma sucursal. Ruta completa: roseta → patch panel → primer equipo activo y de ahí por sus troncales hacia un equipo con al menos tantas troncales (así se detiene en el core y no baja por otro uplink); usa el cableado documentado y los enlaces LLDP. Claves SNMPv3: mínimo 8 caracteres, las dos juntas, cifradas con AES-GCM (`v1:`), nunca devueltas ni auditadas; editar sin enviarlas las conserva. Dar de baja un dispositivo lo saca del gabinete y conserva su cableado e historial. Menú **Red** con Data center (salas → gabinetes → gabinete 2D con arrastrar y soltar), Dispositivos (detalle con puertos por color y panel de puerto), Rosetas y Catálogo.
43. **Importación e historial (W-68, W-69)**: la importación es por CSV (coma, punto y coma o tabulador; UTF-8 o el Windows-1252 de Excel), no se lee `.xlsx` (se guarda "Como CSV" desde Excel; así no se agrega una librería de Excel). Tres tipos: dispositivos, rosetas y cables, nombrados por texto (sucursal, marca y modelo, sala y gabinete, dispositivo y puerto por nombre o número). Cada fila pasa por el mismo alta que el panel dentro de una transacción: la vista previa siempre se deshace y la importación se guarda **todo o nada** (así también choca una fila con otra). Máximo 1000 filas y 2 MB. Las claves SNMP no se importan. El historial se arma con la auditoría de las entidades del data center (más los eventos del sistema: equipo movido, puerto caído), con los campos que cambiaron y las referencias traducidas a nombres; quien tiene sucursales asignadas solo ve lo de los objetos que hoy son de sus sucursales.
44. **Recolector SNMP (4.2)**: cada dispositivo activo tiene un **recolector** (`dispositivos_red.recolector_id`), un agente aprobado de su misma sucursal; `agentes.es_recolector` se marca solo mientras tenga dispositivos asignados. El servidor manda la lista en la `bienvenida` y la vuelve a mandar (`config_actualizada`) al cambiar un dispositivo. Las claves viajan **selladas para ese agente**: AES-256-GCM con llave HKDF-SHA256 del hash de su credencial (el servidor guarda el hash, el agente lo calcula); el agente las abre en memoria justo antes de cada consulta. El agente Node lanza `MonitoreoTI.AgentHost.exe snmp` (el anfitrión le pasa su ruta en `MONITOREOTI_HOST_EXE`) con la petición por la entrada estándar y recibe JSON: sistema, interfaces (ifTable/ifXTable), tabla MAC (Q-BRIDGE o BRIDGE, pasada a ifIndex), ARP y vecinos LLDP. SNMPv3 authPriv con SHA-1 y AES-128 por defecto (el anfitrión también acepta SHA-256 y AES-256). Cada 5 min (`Red:IntervaloSnmpSegundos`), de a un dispositivo y escalonados. Columna nueva `dispositivos_red.ultimo_error` con el motivo del último sondeo fallido (sin datos sensibles), que se muestra en el panel. Los puertos registrados se emparejan con las interfaces físicas en orden de ifIndex y toman su nombre real (Gi1/0/1). Un agente solo puede reportar sus dispositivos asignados; un resultado más viejo que el último no pisa al nuevo. El fabricante de una MAC sale del registro OUI del IEEE si se indica su archivo (`Red:ArchivoOui`); no se incluye en el repositorio. **Tóner de impresoras**: queda pendiente, porque casi ninguna impresora tiene SNMPv3 y la decisión 41 es solo v3 (habría que aceptar v2c de solo lectura para impresoras).
45. **Correlación y alertas de red (4.3)**: estado del puerto: apagado por configuración → deshabilitado; arriba → conectado, o "con errores" si pasa el umbral de advertencia de `puerto_errores` (errores por hora entre dos sondeos); abajo → caído si ahí se vio un equipo conocido en las últimas 24 h, libre si no. Troncal: vecino LLDP registrado o más de 10 MAC sin ser host de VMs; la asignación manual (W-67) manda. Solo se guardan las MAC de puertos de acceso (7 días); se cruzan con las tarjetas de los equipos con agente y con la MAC de la ficha de los equipos sin agente (impresoras). Un equipo que aparece en otro puerto de acceso queda en auditoría como "equipo movido". Enlaces LLDP en `enlaces_red` (se quitan los que ya no se ven). Equipos desconocidos: MAC sin equipo en puertos de acceso; las aleatorias (bit local) se agrupan por puerto y no alertan; "vía access point" si el puerto es de un AP o LLDP ve un AP. Diferencias (calculadas, sin tabla): enlace LLDP sin cable documentado, cable documentado que llega a otro puerto, y equipo detectado en un puerto sin cable documentado. Motor de alertas de red junto al de equipos: `switch_sin_respuesta` (minutos desde el último sondeo bueno, solo si tiene recolector, IP y claves), `puerto_errores` y `puerto_caido` (también ligada al equipo, para su ficha y su semáforo); el estado del dispositivo sale de sus alertas. Evento SignalR `puertoEstadoCambio`. Las alertas de red entran en la lista de alertas con su dispositivo y sucursal (filtro `red=true`) y respetan el alcance por sucursal. Asignación manual de un puerto (W-67): un equipo (por su MAC) o troncal/acceso; "automático" lo devuelve al recolector. La ficha muestra la conexión de red (switch, puerto, VLAN).
46. **Vistas (W-42, W-43, W-44, W-51, W-59)**: Red tiene Resumen, Data center (gabinetes, plano y 3D), Dispositivos, Rosetas, Topología, Desconocidos, Diferencias, Buscar MAC/IP, Catálogo, Historial e Importar. 3D con `three`, `@react-three/fiber` 8 y `@react-three/drei` 9 (lista §4.3; versiones para React 18), cargado aparte solo al abrirlo; si el equipo no tiene WebGL se avisa. Las vistas 3D van dentro del panel, no en una ventana separada (§12.2): se moverán a su ventana cuando existan las ventanas extra (Fase 7). La topología es un mapa en capas (core arriba) con enlaces LLDP continuos y documentados punteados.

**Cierre de la Fase 3 (2026-10-30)**

47. **Tóner de impresoras por SNMP**: se cambia la decisión 41 solo para impresoras: cada impresora usa **v3 o v2c** (comunidad de solo lectura), según lo que soporte; los switches y routers siguen siendo solo v3. Columnas nuevas en `maquinas`: `snmp_version`, `snmp_usuario`, `snmp_credencial_cifrada` (comunidad o claves, AES-GCM, nunca devueltas ni auditadas), `recolector_id` (agente de la misma sucursal), `ultimo_sondeo_snmp` y `ultimo_error_snmp`. El recolector las recibe con su versión y su origen ("impresora") y solo pide los consumibles (`prtMarkerSupplies` descripción, clase, nivel y máximo). Cada consumible queda como componente `toner` con el porcentaje restante; el depósito de residuos (clase 4) se muestra pero no cuenta para la regla. La regla de tóner (menos de 20 % advertencia, menos de 5 % crítico, ajustable por empresa y sucursal) entra al motor de alertas, que ahora también evalúa los equipos sin agente que tienen recolector; un sondeo bueno cuenta como reporte de la impresora.
48. **Notificaciones**: el correo de una alerta crítica (de equipos o de red) se manda **una sola vez** (`alertas.notificada`) a los usuarios activos con `alertas.gestionar` que ven la sucursal (el Administrador ve todas), salvo quien lo desactivó en el menú de su usuario (`usuarios.notificar_correo`, `GET/PUT /api/v1/auth/preferencias`). Se envía con `System.Net.Mail` (sin librerías nuevas) si está configurado `Correo:Smtp` (Host, Port, UseTls, User, Password, From; la clave en user-secrets o variables de entorno); si no, en desarrollo se guarda como archivo. Las alertas que ya existían quedan marcadas como avisadas. En el panel: la campana muestra las alertas abiertas más recientes, y cada alerta crítica nueva genera un aviso nativo de Windows (IPC `notification:show` validado en el proceso principal); al hacerle clic se trae la ventana y se abre la alerta. El push al móvil queda para la Fase 5.
49. **Calendario de mantenimiento (W-27)**: menú **Mantenimiento** con el mes en semanas de lunes a domingo; preventivos programados, correctivos hechos y los vencidos resaltados (con su total). Se programa desde el calendario o desde la ficha del equipo; se marca como hecho con una nota opcional. Ver: permisos de tickets; programar y marcar: `tickets.gestionar`. Un mantenimiento hecho no se elimina. Cada cambio se audita sobre el equipo y aparece en su historial.

**Fases 6 y 7 — Gestión y escritorio remoto (2026-10-30)**

50. **Plan de las fases 6 y 7** (aprobado por el usuario). Fase 6: 6.1 scripts y comandos firmados (W-32, W-33), 6.2 relay y sesiones (W-29, W-34), 6.3 terminal (W-30), 6.4 explorador de archivos (W-31), 6.5 tareas programadas, 6.6 actualización automática del agente. Fase 7: 7.1 asistente de sesión, 7.2 pantalla V1 (W-28), 7.3 control, 7.4 pantalla de inicio y UAC, 7.5 pantalla V2, 7.6 vista 3D en ventana separada (pendiente de la Fase 4). La app móvil (Fase 5, incluida M-21) queda para después.
51. **Terminal, captura y control en el anfitrión C#** (`MonitoreoTI.AgentHost`, por P/Invoke, sin librerías nativas de Node): reemplaza en parte la decisión 1; el modo UI de Electron ya no cumple el papel de `SessionHelper`. ConPTY se lanza como `MonitoreoTI.AgentHost.exe pty`; el asistente de sesión lo lanza el servicio C# en la sesión del usuario activo.
52. **Reglas del acceso remoto**: la terminal corre como **SYSTEM**. Pedir permiso al usuario del equipo antes de conectar está **apagado por defecto** y es configurable por empresa; el aviso de sesión activa se muestra siempre. Para abrir una sesión (escritorio, terminal o archivos) o ejecutar un script, el último 2FA del técnico debe tener **menos de 8 horas**; si no, se pide el código otra vez.
53. **Actualizaciones del agente** firmadas con una **llave del producto** (ECDSA P-256, una para todas las empresas): la privada en user-secrets o variables de entorno, la pública incluida en el agente.
54. **Librerías autorizadas**: `@xterm/addon-fit` (panel) y `Vortice.Windows` (MIT; DXGI y Media Foundation para la pantalla V2 en el anfitrión C#).
55. **Scripts y comandos firmados (6.1, W-32 y W-33)**: la llave ECDSA P-256 de la empresa se crea la primera vez que se necesita (privada cifrada con AES-GCM) y llega a los agentes en la `bienvenida` (`llave_empresa`); el agente la guarda una sola vez y no la reemplaza desde la red. El servidor firma, por comando, el texto `monitoreoti/comando/v1 | comando | equipo | tipo | lenguaje | timeout | vence | SHA-256 del contenido` (IEEE P1363): la firma no sirve para otro equipo ni después de vencer (24 h). El agente rechaza lo no firmado, lo de otro equipo, lo vencido y lo repetido (ids guardados), y el rechazo queda en auditoría como acción del sistema. Los scripts de la biblioteca también se guardan firmados: uno cambiado fuera del panel no se ejecuta. Columnas nuevas: `usuarios.ultimo_2fa`; `scripts.descripcion`, `actualizado`; `comandos.lote_id`, `lenguaje`, `timeout_s` (10 a 3600 s), `vence`, `enviado`, `codigo_salida`. Ejecutar exige `scripts.ejecutar`, el módulo `remoto` en el plan y un 2FA de menos de 8 horas (`POST /api/v1/auth/confirmar-2fa`; el código malo responde 400, no 401). Lenguajes que corre el agente: PowerShell, CMD y Python (si está instalado); salida recortada a 1 MB; la auditoría guarda el hash del contenido, no el contenido. Pantalla **Acceso remoto** con Ejecuciones y Biblioteca; "Ejecutar script" también desde la ficha y desde la selección de la lista de equipos.
56. **Relay y sesiones (6.2, W-29 y W-34)**: `POST /api/v1/sesiones-remotas {maquinaId, tipo}` exige el permiso del tipo (`remoto.terminal`, `remoto.archivos`, `remoto.conectar`), el módulo remoto y el 2FA de menos de 8 horas, y que el agente esté conectado (si no, 409 y no queda registro). Crea `sesiones_remotas`, manda `abrir_sesion_remota` al agente con su token y devuelve al panel otro token distinto (un solo uso, 60 s). Ambos se conectan a `/relay/{id}` y mandan el token como **primer mensaje** (nunca en la URL, para que no quede en logs). El relay reenvía los marcos tal cual (texto y binario) y no registra su contenido; al panel solo le habla con `{"relay":"conectado"|"cerrada"}`. Termina cuando cualquiera cierra, al pulsar cerrar en el panel o si la otra parte no llega en 60 s; registra el fin y la duración (auditoría `sesion.cerrar`) y avisa al agente con `cerrar_sesion_remota`. Las sesiones que quedan abiertas en la base sin estar en el relay (reinicio del servidor) se cierran solas al minuto. El agente solo se conecta a `/relay/{n}` de su propio servidor. Historial en **Acceso remoto → Historial de sesiones**; en la ficha se habilitan Terminal y Archivos (el visor de cada uno llega en 6.3 y 6.4).
57. **Terminal remota (6.3, W-30)**: el anfitrión C# abre PowerShell (por defecto) o CMD en una pseudoconsola (`MonitoreoTI.AgentHost.exe pty`, ConPTY por P/Invoke, como SYSTEM, carpeta inicial la raíz del sistema). El agente le pasa por stdin mensajes `[tipo:1][largo:4][datos]` (1 = teclas, 2 = tamaño) y reenvía su stdout tal cual. Por el relay, el panel manda `{"tipo":"iniciar","shell","columnas","filas"}` y `{"tipo":"tamano"}` en texto y las teclas en binario; el agente responde `listo`, `fin` (con el código de salida) o `error` en texto y la salida en binario. En el panel, `@xterm/xterm` 5.5 con `@xterm/addon-fit` 0.10 en una **ventana propia**: la ventana principal pide la sesión (permiso, 2FA) y entrega el token al proceso principal, que abre la ventana de la terminal y se lo da una sola vez (validado: `/relay/{la misma sesión}` y un token con formato). Cerrar la ventana o pulsar Desconectar cierra la sesión. xterm.js se carga solo en esa ventana.
58. **Explorador de archivos (6.4, W-31)**: lo atiende el agente Node (servicio, como SYSTEM: ve todo el disco). Por el relay, pedidos en texto con un id (`listar`, `descargar`, `ack`, `subir`, `subir_fin`, `cancelar`, `crear_carpeta`, `renombrar`, `eliminar`) y partes binarias `[id:4][hasta 256 KB]`, con **control de flujo** de 4 partes sin confirmar en cada sentido. Una subida se escribe en `<nombre>.monitoreoti-parte` y solo toma su nombre al llegar completa; no pisa un archivo existente salvo que el técnico confirme reemplazarlo. No se elimina una unidad completa. Cada descarga, subida, carpeta nueva, cambio de nombre o eliminación la informa el agente (`actividad_sesion`: ruta, destino y tamaño, nunca el contenido) y queda en auditoría como `archivo.<acción>` a nombre del técnico de la sesión; solo cuenta si la sesión es de archivos y del equipo de ese agente. En el panel, el explorador abre dentro del diálogo de la sesión; el disco local solo lo toca el proceso principal (diálogo del sistema para elegir dónde guardar o qué subir, escritura y lectura por partes, transferencias de la propia ventana; una descarga a medias se borra).
59. **Tareas programadas (6.5)**: tabla `tareas_programadas` (script de la biblioteca; objetivo: equipos elegidos, una sucursal o todos; frecuencia una vez, diaria, semanal con días marcados o mensual —si el mes es más corto, su último día—; hora; tiempo límite; activa). Las horas son de **República Dominicana (UTC−4, sin horario de verano)**; la próxima ejecución se guarda en UTC. Cada minuto, empresa por empresa, se ejecutan las que tocan: se crea el lote de comandos firmados como una ejecución manual, **a nombre de quien creó la tarea**, y queda en auditoría (`tarea.ejecutar`, con el hash del contenido). No se ejecuta si el plan perdió el módulo remoto o si la firma del script ya no es válida. Una tarea de una sola vez queda en pausa después de ejecutarse. Crear o editar exige `scripts.ejecutar`, el módulo remoto y el 2FA reciente; quien ve solo algunas sucursales no programa "todos los equipos". Un script usado por una tarea no se puede eliminar. Pantalla: **Acceso remoto → Tareas programadas**.
60. **Actualización automática del agente (6.6)**: la versión publicada es el instalador de `Instaladores:Archivo` (su versión va incrustada en el .exe); el servidor firma `monitoreoti/actualizacion/v1 | versión | SHA-256` con la **llave del producto** (`Actualizaciones:LlavePrivada`, user-secrets). A los agentes conectados y atrasados se les manda `actualizar_agente` por **tandas de 10 por empresa** (cada uno cuenta 15 minutos o hasta que vuelve con la versión nueva); se ofrece al conectarse y cada minuto, si la empresa tiene `empresas.actualizacion_automatica` (activa por defecto), o a mano con "Actualizar ahora". El agente verifica la firma con la llave pública incluida, descarga de `/agent/actualizacion` con su credencial, comprueba tamaño y SHA-256, deja `update-pending.json` y termina con el código 75; el **anfitrión C# vuelve a verificar** (archivo dentro de `updates`, hash y firma) y ejecuta el instalador con `/S`, que conserva la configuración y la credencial. Si el instalador falla, el anfitrión sigue con la versión actual y el servidor la vuelve a ofrecer más tarde. **Cambio frente al plan**: no hay vuelta atrás automática a la versión anterior (el equipo no guarda el instalador viejo); la protección es la firma, el hash y el despliegue por tandas. La versión del agente sube a **0.2.0**; los agentes 0.1.0 no tienen el actualizador y deben reinstalarse a mano una vez. Pantalla: **Instaladores → Actualización del agente**.

**Fase 7 — Escritorio remoto (2026-10-31)**

61. **Asistente de sesión, pantalla V1 y control (7.1 a 7.3, W-28)**: el agente Node (SYSTEM, sesión 0) atiende las sesiones `escritorio` lanzando `MonitoreoTI.AgentHost.exe escritorio`, que abre el asistente (`... asistente`) en la sesión de quien tiene la consola (`WTSQueryUserToken` + `CreateProcessAsUser`, escritorio `winsta0\default`) y pasa los mensajes por stdin/stdout (`[tipo:1][largo:4][datos]`, 1 = texto, 2 = binario), que el agente reenvía tal cual por el relay. El asistente: captura con GDI (`BitBlt`, con el cursor dibujado, "per-monitor DPI aware") en mosaicos de 64×64, compara con la captura anterior y manda en JPEG (GDI+ de Windows, **sin librerías nuevas**) solo los tramos que cambiaron, como mucho cada 50 ms y con **2 actualizaciones sin confirmar** (el visor confirma cada una al dibujarla); mouse y rueda con `SendInput` en coordenadas absolutas del escritorio virtual; teclado por **tecla física** (`KeyboardEvent.code` → código de exploración: el carácter lo decide la distribución del equipo remoto); portapapeles de texto en ambos sentidos a pedido ("Pegar en el equipo", "Copiar del equipo"); calidad (rápida 35, normal 60, nítida 85) y monitor elegibles. El asistente espera el `{"tipo":"ver"}` del visor antes de mandar la pantalla. **Aviso**: barra siempre visible arriba al centro ("Sesión remota activa: <técnico>…") con el botón **Terminar**. **Permiso**: `empresas.pedir_permiso_remoto` (apagado por defecto; lo cambia quien tiene `usuarios.administrar`, en Acceso remoto → Historial de sesiones): el usuario debe aceptar en 30 segundos o no se conecta (solo escritorio; la terminal y los archivos no muestran nada en el equipo). El panel abre el escritorio en su **propia ventana** (como la terminal), con la imagen escalada a la ventana y las teclas soltadas al perder el foco. Sin usuario con sesión iniciada no hay escritorio (la pantalla de inicio y UAC llegan en 7.4). La versión del agente sube a **0.3.0**.
62. **Servidor con certificado propio (red local y empresas sin certificado público)**: se mantiene la regla de solo https (salvo `localhost`). El anfitrión pasa al agente `NODE_EXTRA_CA_CERTS` si existe `ca-confianza.pem` en la carpeta de datos (Node no usa el almacén de Windows), y el instalador la deja ahí si encuentra `MonitoreoTI-CA.pem` junto al `.exe` (las actualizaciones automáticas la conservan). En desarrollo: CA "MonitoreoTI Desarrollo CA" y certificado del servidor para `192.168.3.115` en el almacén del usuario (fuera del repositorio, la CA pública en `%USERPROFILE%\MonitoreoTI-dev\certificados`); el API escucha en `http://localhost:5126`, `http://192.168.3.115:5126` y `https://192.168.3.115:5127` (puntos de Kestrel en user-secrets) e `Instaladores:UrlServidor` apunta al https. La versión del agente sube a **0.3.1**.
63. **Pantalla de bloqueo, UAC y Ctrl+Alt+Supr (7.4)**: el lanzador abre el asistente con el **token de SYSTEM** (el suyo, con la sesión cambiada a la de la consola con `SetTokenInformation(TokenSessionId)`) en lugar del token del usuario, porque solo así puede entrar al escritorio seguro. Funciona aunque nadie haya iniciado sesión (pantalla de inicio). La captura y el control corren en un hilo propio que en cada vuelta revisa el escritorio que recibe la entrada (`OpenInputDesktop`: Default, Winlogon o el de UAC), se une a él (`SetThreadDesktop`) si cambió, rehace la captura y avisa al visor (`{"tipo":"escritorio","nombre"}`), que lo muestra en su barra. Las entradas se ejecutan en ese hilo y en orden. **Ctrl+Alt+Supr** con `SendSAS` (botón en la barra del visor); la política `SoftwareSASGeneration` la deja el instalador (y el asistente si falta). Aviso y permiso del usuario: solo si hay alguien con la sesión iniciada y en su escritorio normal; con el equipo bloqueado o sin nadie conectado no hay a quién preguntar (equipo desatendido) y la barra de aviso no puede mostrarse sobre la pantalla de bloqueo (Windows no lo permite). La versión del agente sube a **0.3.2**.
64. **Escritorio remoto según los diseños W-28a a W-28f, W-29 y W-29b**: **Antes de conectar** (W-28a): modo control total o solo ver, calidad (automática, alta, equilibrada, rápida), motivo (columnas nuevas `sesiones_remotas.motivo` y `solo_ver`; quedan en la auditoría y en el historial) y, por sesión, pedir permiso al usuario (siempre, si la empresa lo exige) y sincronizar el portapapeles. **Conectando** (W-29): pasos agente en línea, canal establecido, esperando permiso de <usuario> con su cuenta regresiva (el asistente espera al visor antes de preguntar) e iniciando transmisión. **Error** (W-29b): qué pasó, causas probables según el código (`ERR_RELAY_TIMEOUT`, `PERMISO_DENEGADO`, `ERR_SESION_CERRADA`...), último reporte, código y hora, Reintentar (la ventana pide a la principal otra sesión con las mismas opciones). **Barra flotante**: equipo y tiempo, Ctrl+Alt+Supr, portapapeles (sincronizar, lo último copiado en el equipo, "Escribirlo" con teclas Unicode o "Pegar"), teclas especiales, ajuste (a la ventana, tamaño real, estirar; monitor 1, 2… o todos; pantalla completa con F11), calidad (automática según lo que tarda cada actualización; latencia por ping, cuadros y Mb/s), archivos, captura de pantalla (PNG), terminal y **Más**: bloquear teclado y mouse del usuario (`BlockInput`), pantalla en negro para el usuario (ventana negra excluida de la captura con `SetWindowDisplayAffinity`, Windows 10 2004 o posterior; si no se puede, no se muestra), explorador y administrador de tareas, cambiar de usuario, cerrar sesión y reiniciar (con confirmación y en auditoría como `escritorio.<acción>`), y cambiar a solo ver (sin vuelta atrás en la sesión; el asistente ignora la entrada). **Archivos dentro del escritorio** (W-28e): panel lateral con arrastrar para enviar; va por la misma sesión (partes binarias con el marcador 2; las de pantalla empiezan con 1), solo con `remoto.archivos`, y cada operación queda en auditoría. **Barra de estado**: sesión, técnico → usuario, modo, resolución y ajuste, calidad, latencia y cuadros, y si la conexión va cifrada (TLS) o no. El **aviso en el equipo** pasa a ser una tarjeta abajo a la derecha ("Soporte de TI está conectado… puede ver y controlar"). **Queda para después**: grabar la sesión, audio, ocultar el fondo, chat, encender por la red (WoL), reiniciar en modo seguro y cambiar la resolución del equipo. La versión del agente sube a **0.3.3**.
65. **Usuarios, roles y Mi perfil (W-36 a W-38, W-41)**. **Usuarios** (`/api/v1/usuarios`, `usuarios.administrar`): lista con rol, sucursales, 2FA, último acceso y estado (activo, sin contraseña, bloqueado, inactivo); crear, editar, activar y desactivar. El administrador **no escribe contraseñas**: al crear se envía un enlace de un solo uso para crearla (`tokens_restablecimiento`, vence en **72 horas**, `Auth:PasswordInvitationLifetime`), y "Restablecer contraseña" manda otro (anula los anteriores y quita el bloqueo por intentos; al usarlo se cierran sus sesiones). Si el servidor no envía correos de verdad (archivo de desarrollo o sin SMTP, `IEmailSender.Delivers`), el enlace vuelve al panel una sola vez para entregarlo a mano; nunca queda en la auditoría. "Reiniciar 2FA" borra el secreto y cierra sus sesiones: al entrar vuelve a ver el QR. Los usuarios no se borran: **desactivar** apaga su pertenencia a la empresa (`usuario_empresa.activo`) y cierra sus sesiones de esa empresa. Un correo que ya existe en otra empresa es la misma persona: se suma a esta con su contraseña de siempre, y como **cuenta compartida** aquí solo se cambian su rol, sus sucursales y si está activo (su nombre, correo, contraseña y 2FA solo los cambia él), para que una empresa no pueda tomar la cuenta que alguien usa en otra. Reglas: nadie se desactiva ni se cambia el rol a sí mismo; siempre queda al menos un **Administrador** activo; se respeta `planes.max_usuarios`; quien ve solo algunas sucursales solo asigna esas. **Roles y permisos** (`/api/v1/roles`, `/api/v1/permisos`): matriz por módulo; los 5 roles del sistema se ven y se **duplican** pero no se cambian (el Administrador conserva siempre todos los permisos); los roles propios se crean, editan y se eliminan solo si nadie los tiene; los permisos de acceso remoto se marcan porque obligan al 2FA. Un cambio de rol o de permisos llega en la siguiente renovación (15 minutos). **Mi perfil** (menú del usuario; `/api/v1/auth/perfil`, `/contrasena`, `/sesiones`): nombre, cambiar la contraseña (pide la actual y cierra las demás sesiones; limitado como el login), estado del 2FA, avisos por correo (antes en el menú) y **sesiones abiertas** (refresh tokens vigentes, con IP y equipo; se cierran una o todas menos la actual). Columna nueva `refresh_tokens.dispositivo` (pasa a cada renovación): el panel agrega `Equipo/<nombre del PC>` a su User-Agent y el servidor guarda solo "Centinela en <PC> (Windows)", "Navegador"… El token de acceso lleva `sid` (su refresh) para marcar la sesión actual. **Robo de refresh**: solo un refresh ya **rotado** que vuelve a aparecer cierra todas las sesiones; uno cerrado a propósito (cerrar sesión, Mi perfil, desactivado) solo deja de servir. **Datos de desarrollo**: se borraron los datos de prueba de la base `MonitoreoTI` (quedan Caribe Tours, la sucursal Santo Domingo, PC-042076 y PC-042077, Ana Martínez y el superadministrador); `03_seed_dev.sql` no cambia porque lo usan las pruebas.
66. **Fase 7 completa: pantalla V2 (7.5) y vista 3D en ventana (7.6)**. **Pantalla V2**: el asistente copia el monitor con la duplicación de escritorio de **DXGI**, dibuja el cursor, convierte a NV12 (BT.709, rango limitado) y lo comprime en **H.264** con el codificador de Media Foundation de Windows (perfil base, sin cuadros B, baja latencia, CBR, cuadro clave cada 10 s y a pedido) a 30 cuadros por segundo. Calidad → ancho de banda: 35 ≈ 1,2 Mb/s, 60 ≈ 2,5 Mb/s y 85 ≈ 5 Mb/s en 1920 × 1080; la automática lo ajusta en el momento con `ICodecAPI` (declarada a mano: Vortice no la trae). Mensaje binario `[3][ancho:2][alto:2][clave:1]` + Annex B (el 1 sigue siendo JPEG y el 2 las partes de archivo). El visor la pide con `{"tipo":"ver","video":["h264"]}` solo si WebCodecs la decodifica, configura el decodificador con el codec exacto del SPS, confirma cada cuadro al dibujarlo (y a más tardar en 1 s) y ante un cuadro dañado pide uno clave. Se sigue con la **V1** (JPEG) si el visor no decodifica H.264, al ver "todos los monitores", con un monitor girado, en una sesión de escritorio remoto, sin codificador, o tras tres fallas seguidas (hasta que cambie el escritorio o el monitor). El menú de calidad dice cuál se usa. Diagnóstico para soporte: `MonitoreoTI.AgentHost.exe prueba-pantalla [--archivo x.h264]`. Paquetes `Vortice.Direct3D11` y `Vortice.MediaFoundation` 3.8.3 (autorizados en la decisión 54). **Vista 3D en ventana (sección 12.2)**: botón "3D en ventana" en la sala y en el gabinete; una ventana por sala o gabinete (`#vista3d/sala-{id}`). La ventana no tiene sesión: la principal carga los gabinetes y se los manda por el proceso principal (validados, hasta 2 MB), otra vez cada vez que cambian; un clic en un equipo lo abre en Red en la ventana principal. **Barra del escritorio remoto**: ocupa todo el ancho antes de pasar a otra fila y, en ventanas estrechas, muestra solo los íconos. **Servicios detenidos**: el agente manda el código de salida de cada servicio detenido; uno que termina con **0** (actualizadores, servicios por demanda, o detenido a propósito) no es una caída, y su alerta se resuelve sola. Los agentes viejos no lo mandan y siguen como antes.
67. **Configuración (W-40)**, con el permiso `configuracion.editar` (el Administrador lo tiene; se puede dar a otros roles). Todo cambio queda en la auditoría con el antes y el después. Pestañas: **Empresa y plan** (nombre —único en el sistema—, RNC de 9 u 11 dígitos, correo y teléfono; el plan en solo lectura con agentes, sucursales y usuarios usados frente al máximo y sus módulos: el plan lo cambia el proveedor en la Fase 8). **Apariencia**: logo PNG, JPEG o WebP de hasta 256 KB (el tipo se deduce de los bytes; **nunca SVG**, que puede llevar código; en la auditoría va el tipo y el tamaño, no la imagen) y **color de la empresa**, que reemplaza a `--color-primary`, su tono de hover y el texto encima en tema claro y oscuro; el panel ajusta el tono hasta que se lea (contraste 4,5:1 contra la superficie) y los colores de estado no cambian. Columnas nuevas `empresas.logo`, `logo_tipo` y `color_primario`. `GET /api/v1/apariencia` (cualquier usuario de la empresa) entrega el color y el logo como data URL; el panel lo pinta al entrar y al cambiar de empresa, muestra el logo en el menú lateral y guarda la última apariencia para la pantalla de carga y las ventanas de sesión; sin sesión vuelven los colores de Centinela. **Departamentos**: crear y renombrar (nombre único en la empresa); se elimina solo si no tiene equipos (contando los dados de baja). **Seguridad y remoto**: pedir permiso al usuario antes de ver su pantalla y la actualización automática del agente (los mismos ajustes, con la misma auditoría, que siguen en Acceso remoto e Instaladores). **Correo y avisos**: si el servidor envía por SMTP (servidor y remitente; nunca usuario ni clave), los guarda como archivo (desarrollo) o no tiene correo; "Enviarme un correo de prueba" (uno por minuto por usuario, a su propio correo; si falla, en el registro va solo el tipo de error). La API está en `/api/v1/configuracion` (empresa, apariencia/color, apariencia/logo, departamentos, seguridad, correo, correo/prueba).
68. **Animaciones y verificación en dos pasos (W-02)**, solo con CSS (sin librerías) en `desktop/src/renderer/src/animations.css`, todas dentro de `prefers-reduced-motion: no-preference`: con "reducir animaciones" de Windows el panel queda quieto. La página nueva entra subiendo; tarjetas, indicadores y filas aparecen en cascada; ventanas emergentes y menús crecen y se desvanecen; los botones se levantan al pasar el mouse; el menú lateral marca la opción activa con una barra; el subrayado de las pestañas crece; el estado crítico late; barras y medidores se llenan al aparecer; la carga usa un brillo en lugar del pulso; los errores se sacuden. Las alertas que se resuelven se desvanecen antes de quitarse (`useLeaving`, en Alertas y el Dashboard). El código de la app autenticadora (inicio de sesión, su alta y la confirmación cada 8 horas del acceso remoto) se escribe en **seis casillas** (`OtpInput`): el cursor avanza solo, Retroceso vuelve, las flechas mueven, pegar o el autocompletado del sistema llenan todas, y **al completar los 6 dígitos se envía solo**. Comprobando, las casillas brillan en ola; si el código es incorrecto se sacuden en rojo, se borran y el foco vuelve a la primera; si es correcto se ponen verdes, el escudo se vuelve una palomita y se sigue tras 0,65 s (sin pausa con movimiento reducido). Al activar el 2FA hay pasos numerados y un botón para copiar la clave. Cada paso del acceso entra deslizándose.
69. **Reportes (W-35)**, con el permiso `reportes.ver`. Catálogo de tarjetas; cada reporte con filtros de periodo (7, 30 o 90 días o fechas, hasta 400 días; por omisión los últimos 30 con hoy incluido), sucursal y departamento, siempre dentro de las sucursales del usuario. **Básicos** (todos los planes): **inventario** (hardware, sistema, usuario, serie, red y estado; es la foto de hoy), **disponibilidad** (horas con métricas frente a horas del periodo, desde el alta del equipo, y la foto diaria de la flota cuando se ve toda la empresa), **alertas** (totales por nivel, tipo y sucursal, por día, tiempo medio de resolución y los 10 equipos con más alertas; solo alertas de equipos) y **discos y seguridad** (volúmenes con menos de 15 % libre, discos con salud dudosa, equipos sin antivirus —solo los que ya reportaron su seguridad—, con el antivirus apagado o desactualizado o el firewall apagado). **Avanzados** (módulo `reportes_avanzados`, validado en el servidor: sin él, 403 `modulo_no_incluido` aunque se llame a la API; en el panel se ven con candado y "Disponible en el plan Empresarial"): **rendimiento** (CPU y RAM promedio y máximo y disco máximo por equipo desde `metricas_hora`, con la tendencia diaria y las recomendaciones *ampliar RAM* ≥ 85 %, *revisar CPU* ≥ 80 % y *liberar disco* ≥ 90 %), **software** (programas con cuántos equipos y qué versiones; al buscar uno, quién lo tiene), **tickets y mantenimiento** (creados, cerrados, abiertos, tiempo medio de solución, por prioridad, carga por técnico y mantenimientos hechos, pendientes y vencidos; los tickets sin equipo solo cuentan viendo toda la empresa) y **accesos remotos** (sesiones por técnico y equipo con duración, modo y motivo). **Exportar CSV** sin librerías (marca UTF-8 para Excel, comillas escapadas y un apóstrofo delante de lo que Excel tomaría como fórmula) e **Imprimir / PDF** con el diálogo del sistema: la hoja lleva el logo, el nombre de la empresa, el periodo y la fecha. API `/api/v1/reportes` (qué puede abrir la empresa) y `/api/v1/reportes/{inventario, disponibilidad, alertas, discos-seguridad, rendimiento, software, tickets, remoto}`.
70. **Detalles del panel (sección 12.2)**. **Bandeja**: ícono dibujado en el proceso principal (sin archivos) con un punto rojo si hay alertas críticas abiertas (la ventana principal manda la cantidad por IPC validado); menú Abrir Centinela, *N alertas críticas* (abre Alertas), Buscar equipo, Iniciar con Windows (arranca en la bandeja con `--oculto`; solo en la app instalada) y Salir. **Cerrar la ventana la manda a la bandeja** (el panel sigue avisando de alertas críticas); la primera vez un aviso lo explica; se cierra del todo con Salir, al instalar una actualización o al apagar Windows. **Enlaces profundos** `monitoreoti://maquina/{id}`, `monitoreoti://q/{codigo}` y `monitoreoti://restablecer?token=…`: los valida el proceso principal (todo lo demás se descarta), llegan al abrir el panel o a la instancia que ya corre, y el panel los atiende cuando puede: sin sesión, el de restablecer abre "Contraseña nueva" con el enlace puesto y los demás esperan a que se inicie sesión. Nuevo `GET /api/v1/qr/{codigo}` (con sesión): dice si el código es un equipo de las sucursales del usuario o, con `red.ver`, un dispositivo, gabinete o roseta (404 si no existe o no lo puede ver; lo usará también la app). **Instalador** `MonitoreoTI-Panel-Setup-{versión}.exe` (electron-builder + NSIS, por usuario y sin administrador, accesos directos, registra `monitoreoti://`); se genera con `MONITOREOTI_SERVER_URL=… npm run installer -w @monitoreoti/desktop`, que deja el servidor en `resources/panel.json` (la variable de entorno lo reemplaza en desarrollo). Sin certificado, SmartScreen muestra "editor desconocido". **Actualización automática** con `electron-updater` (lista §4.3) desde el mismo servidor: revisa a los 15 s de abrir y cada 4 horas, descarga en segundo plano y muestra "Hay una versión nueva — Reiniciar ahora / Más tarde"; si no se reinicia, se instala al salir. El servidor entrega sin sesión solo `latest.yml` y `MonitoreoTI-Panel-Setup-x.y.z.exe(.blockmap)` de la carpeta `PanelActualizaciones:Carpeta` (con rangos para descargar solo lo que cambió); publicar una versión es subir `version` en `desktop/package.json`, generar el instalador y copiar esos tres archivos de `desktop/release` a esa carpeta.
