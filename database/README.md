# Base de datos

SQL Server 2019+. Los scripts son la única fuente del esquema: EF Core se mapea a ellos (database-first, sin migraciones).

| Script | Contenido |
| --- | --- |
| `00_create_database.sql` | Crea la base (variable sqlcmd `$(DbName)`), intercalación `Modern_Spanish_CI_AS` |
| `01_schema.sql` | 54 tablas multiempresa, índices, vista, procedimiento, planes, roles y permisos |
| `02_multitenant.sql` | Row-Level Security: esquema `seguridad`, predicados y política `seguridad.politica_empresa` |
| `03_seed_dev.sql` | Datos de prueba (solo desarrollo): 2 empresas, sucursales, equipos, data center |
| `reset-db.ps1` | Borra y recrea una base ejecutando los scripts en orden |

## Recrear la base

```powershell
$env:MONITOREOTI_SQL_PASSWORD = '<contraseña de sa>'
$env:MONITOREOTI_APP_PASSWORD = '<contraseña para el login de la aplicación>'
./reset-db.ps1 -Server 192.168.4.117,1433 -User sa
```

- Con `MONITOREOTI_APP_PASSWORD` crea el login `monitoreoti_app` (lectura, escritura y EXECUTE). La API usa ese login,
  nunca `sa`: no puede alterar el esquema ni desactivar la política de seguridad.
- `-Database MonitoreoTI_Tests -SkipSeed` crea otra base con el mismo esquema. Las pruebas de integración del servidor
  recrean `MonitoreoTI_Tests` por su cuenta.

## Multiempresa

- Toda tabla de negocio tiene `empresa_id NOT NULL` y sus índices empiezan por `empresa_id`.
- Las relaciones entre tablas de negocio son FK compuestas `(empresa_id, x_id)`: la base rechaza referencias cruzadas
  entre empresas. Excepción: las FK `ON DELETE SET NULL` (`tabla_mac.maquina_id`, `alertas.regla_id`,
  `comandos.script_id`) quedan simples, porque SET NULL anularía también `empresa_id`.
- Row-Level Security filtra y bloquea por `SESSION_CONTEXT(N'empresa_id')`. El servidor lo fija en cada conexión.
- Usuarios de prueba: ver el encabezado de `03_seed_dev.sql` (contraseña de desarrollo `MonitoreoTI.Dev1`).
- Tokens de registro de agentes de prueba: `<id empresa>.DEV-ENROLL-SANTO-DOMINGO` (Caribe) y
  `<id empresa>.DEV-ENROLL-SAN-RAFAEL`; con la base recién creada, Caribe es la empresa 1.

## Consultar desde SSMS

RLS aplica a todos, incluido `sa`. En cada ventana de consulta ejecute primero uno de estos:

```sql
EXEC sp_set_session_context N'es_superadmin', 1;   -- ver todas las empresas
EXEC sp_set_session_context N'empresa_id', 1;      -- ver solo la empresa 1
```

Para ejecutar `00_create_database.sql` en SSMS active **Query > SQLCMD Mode**.
