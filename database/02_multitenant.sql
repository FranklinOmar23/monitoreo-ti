/* =====================================================================
   02. ROW-LEVEL SECURITY (AISLAMIENTO ENTRE EMPRESAS)
   ---------------------------------------------------------------------
   El servidor fija el contexto en cada conexión que abre:
       EXEC sp_set_session_context N'empresa_id', @empresa_id, @read_only = 1;
       EXEC sp_set_session_context N'es_superadmin', 1, @read_only = 1;  -- solo portal del proveedor

   Sin contexto, las tablas de negocio se ven vacías y no aceptan escrituras.
   RLS aplica a todos los usuarios de la base, incluido sa: para consultar
   desde SSMS, ejecutar primero (en la misma ventana):
       EXEC sp_set_session_context N'es_superadmin', 1;

   La política se arma a partir del catálogo: toda tabla de dbo con
   columna empresa_id queda cubierta, salvo las excepciones listadas.
   Este script es idempotente: volver a ejecutarlo tras agregar tablas
   las incorpora a la política.

   Excepciones:
     - usuario_empresa y refresh_tokens: se leen durante el login y la
       renovación del token, antes de conocer la empresa. Solo las usa el
       servicio de autenticación.
     - roles y modelos_dispositivo: filtran con "empresa o global"
       (empresa_id NULL = registro del sistema, visible para todos y
       modificable solo por el superadministrador).
     - auditoria: permite insertar registros sin empresa (login fallido,
       acciones del superadministrador); solo el superadministrador los ve.
     - empresas: el filtro se aplica sobre la columna id.
   ===================================================================== */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID(N'seguridad') IS NULL
    EXEC (N'CREATE SCHEMA seguridad');
GO

IF OBJECT_ID(N'seguridad.politica_empresa', N'SP') IS NOT NULL
    DROP SECURITY POLICY seguridad.politica_empresa;
GO

/* Registro de la empresa del contexto, o cualquiera si es superadministrador */
CREATE OR ALTER FUNCTION seguridad.fn_predicado_empresa (@empresa_id INT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS permitido
    WHERE @empresa_id = CAST(SESSION_CONTEXT(N'empresa_id') AS INT)
       OR CAST(SESSION_CONTEXT(N'es_superadmin') AS BIT) = 1;
GO

/* Igual que el anterior, pero los registros globales (NULL) son visibles para todos */
CREATE OR ALTER FUNCTION seguridad.fn_predicado_empresa_o_global (@empresa_id INT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS permitido
    WHERE @empresa_id IS NULL
       OR @empresa_id = CAST(SESSION_CONTEXT(N'empresa_id') AS INT)
       OR CAST(SESSION_CONTEXT(N'es_superadmin') AS BIT) = 1;
GO

/* Inserción en auditoría: se aceptan registros sin empresa */
CREATE OR ALTER FUNCTION seguridad.fn_predicado_auditoria_insertar (@empresa_id INT)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS permitido
    WHERE @empresa_id IS NULL
       OR @empresa_id = CAST(SESSION_CONTEXT(N'empresa_id') AS INT)
       OR CAST(SESSION_CONTEXT(N'es_superadmin') AS BIT) = 1;
GO

DECLARE @sql NVARCHAR(MAX) = N'';
DECLARE @tablas_cubiertas INT = 0;

-- Tablas con empresa_id: filtro + bloqueo estricto
SELECT
    @sql = STRING_AGG(CAST(
             N'ADD FILTER PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.' + QUOTENAME(t.name) + N',' + CHAR(10)
           + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.'  + QUOTENAME(t.name) + N','
           AS NVARCHAR(MAX)), CHAR(10)) WITHIN GROUP (ORDER BY t.name) + CHAR(10),
    @tablas_cubiertas = COUNT(*)
FROM sys.tables t
JOIN sys.columns c ON c.object_id = t.object_id AND c.name = N'empresa_id'
WHERE t.schema_id = SCHEMA_ID(N'dbo')
  AND t.name NOT IN (N'usuario_empresa', N'refresh_tokens',
                     N'roles', N'modelos_dispositivo', N'auditoria');

SET @sql = N'CREATE SECURITY POLICY seguridad.politica_empresa' + CHAR(10) + @sql
    -- empresas: cada empresa ve solo su propio registro
    + N'ADD FILTER PREDICATE seguridad.fn_predicado_empresa(id) ON dbo.empresas,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(id) ON dbo.empresas,' + CHAR(10)
    -- catálogos con registros globales
    + N'ADD FILTER PREDICATE seguridad.fn_predicado_empresa_o_global(empresa_id) ON dbo.roles,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.roles,' + CHAR(10)
    + N'ADD FILTER PREDICATE seguridad.fn_predicado_empresa_o_global(empresa_id) ON dbo.modelos_dispositivo,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.modelos_dispositivo,' + CHAR(10)
    -- auditoría
    + N'ADD FILTER PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.auditoria,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_auditoria_insertar(empresa_id) ON dbo.auditoria AFTER INSERT,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.auditoria AFTER UPDATE,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.auditoria BEFORE UPDATE,' + CHAR(10)
    + N'ADD BLOCK PREDICATE seguridad.fn_predicado_empresa(empresa_id) ON dbo.auditoria BEFORE DELETE' + CHAR(10)
    + N'WITH (STATE = ON, SCHEMABINDING = ON);';

EXEC sys.sp_executesql @sql;

PRINT CONCAT(N'Política seguridad.politica_empresa creada: ', @tablas_cubiertas + 4,
             N' tablas con aislamiento por empresa.');
GO
