/* =====================================================================
   SISTEMA DE MONITOREO DE MÁQUINAS, RED Y DATA CENTER (MULTIEMPRESA)
   Script de creación del esquema — SQL Server 2019 o superior
   ---------------------------------------------------------------------
   Contenido:
     1. Empresas, planes y suscripciones   (5 tablas)
     2. Usuarios y seguridad                (7 tablas)
     3. Organización e inventario           (9 tablas)
     4. Red y data center                   (10 tablas)
     5. Monitoreo y alertas                 (6 tablas)
     6. Soporte y gestión remota            (8 tablas)
     7. Índices
     8. Vista de ubicación de red
     9. Procedimiento de resumen y limpieza de métricas
    10. Datos iniciales (planes, roles y permisos)

   Orden de ejecución:
     00_create_database.sql -> 01_schema.sql -> 02_multitenant.sql -> 03_seed_dev.sql
     Ejecutar este script conectado a la base ya creada y vacía.

   Convenciones:
     - Todas las fechas se guardan en UTC (SYSUTCDATETIME()).
     - Máquinas, dispositivos y usuarios NO se borran: se marcan con
       estado 'baja' o activo = 0, para conservar el historial.
     - Solo las tablas de detalle (métricas, componentes, puertos, etc.)
       se borran en cascada cuando se borra su tabla padre.

   Multiempresa:
     - Toda tabla de negocio tiene empresa_id NOT NULL.
       Excepciones con NULL permitido: roles y modelos_dispositivo
       (NULL = global del sistema), auditoria y refresh_tokens
       (NULL = acción del superadministrador o del sistema).
     - Tablas globales sin empresa_id: planes, plan_modulos, permisos,
       rol_permiso, usuarios (un usuario puede estar en varias empresas
       mediante usuario_empresa).
     - Las relaciones entre tablas de negocio usan FK compuestas
       (empresa_id, x_id): la base rechaza que un registro de una empresa
       apunte a un registro de otra. Las FK con ON DELETE SET NULL quedan
       simples porque SET NULL anularía también empresa_id.
     - Los UNIQUE de negocio son únicos por empresa.
     - Row-Level Security se aplica en 02_multitenant.sql.
   ===================================================================== */

-- Necesarios para los índices filtrados (si se ejecuta con sqlcmd)
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


/* =====================================================================
   1. EMPRESAS, PLANES Y SUSCRIPCIONES
   ===================================================================== */

CREATE TABLE dbo.planes (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    nombre          NVARCHAR(50)  NOT NULL UNIQUE,
    max_agentes     INT NULL CHECK (max_agentes >= 0),      -- NULL = sin límite
    max_sucursales  INT NULL CHECK (max_sucursales >= 0),
    max_usuarios    INT NULL CHECK (max_usuarios >= 0),
    precio_mensual  DECIMAL(10,2) NOT NULL DEFAULT 0 CHECK (precio_mensual >= 0),
    activo          BIT NOT NULL DEFAULT 1
);
GO

-- Módulos que incluye cada plan
CREATE TABLE dbo.plan_modulos (
    plan_id         INT NOT NULL REFERENCES dbo.planes(id) ON DELETE CASCADE,
    modulo          VARCHAR(30) NOT NULL
                    CHECK (modulo IN ('monitoreo','remoto','red','reportes_avanzados')),
    CONSTRAINT PK_plan_modulos PRIMARY KEY (plan_id, modulo)
);
GO

CREATE TABLE dbo.empresas (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    nombre          NVARCHAR(150) NOT NULL UNIQUE,
    rnc             VARCHAR(15)   NULL,                 -- RNC o cédula, sin guiones
    correo_contacto NVARCHAR(200) NULL,
    telefono        VARCHAR(20)   NULL,
    plan_id         INT NOT NULL REFERENCES dbo.planes(id),
    estado          VARCHAR(15) NOT NULL DEFAULT 'activa'
                    CHECK (estado IN ('activa','suspendida','cancelada')),
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.suscripciones (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    plan_id         INT NOT NULL REFERENCES dbo.planes(id),
    inicio          DATE NOT NULL,
    fin             DATE NULL,                          -- NULL = vigente sin fecha de fin
    estado          VARCHAR(15) NOT NULL DEFAULT 'activa'
                    CHECK (estado IN ('activa','vencida','cancelada')),
    CHECK (fin IS NULL OR fin >= inicio)
);
GO

-- Llaves ECDSA P-256 de cada empresa para firmar comandos y scripts
CREATE TABLE dbo.empresa_llaves (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id              INT NOT NULL REFERENCES dbo.empresas(id),
    llave_publica           NVARCHAR(500)  NOT NULL,    -- SubjectPublicKeyInfo en Base64
    llave_privada_cifrada   VARBINARY(MAX) NOT NULL,    -- cifrada por la aplicación (AES-GCM)
    creada                  DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    activa                  BIT NOT NULL DEFAULT 1
);
GO


/* =====================================================================
   2. USUARIOS Y SEGURIDAD
   ===================================================================== */

-- empresa_id NULL = rol del sistema disponible para todas las empresas
CREATE TABLE dbo.roles (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NULL REFERENCES dbo.empresas(id),
    nombre          NVARCHAR(50)  NOT NULL,
    descripcion     NVARCHAR(200) NULL,
    CONSTRAINT UQ_roles_empresa_nombre UNIQUE (empresa_id, nombre)
);
GO

CREATE TABLE dbo.permisos (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    clave           NVARCHAR(100) NOT NULL UNIQUE,   -- ej. 'remoto.conectar'
    descripcion     NVARCHAR(200) NULL
);
GO

CREATE TABLE dbo.rol_permiso (
    rol_id          INT NOT NULL REFERENCES dbo.roles(id)    ON DELETE CASCADE,
    permiso_id      INT NOT NULL REFERENCES dbo.permisos(id) ON DELETE CASCADE,
    CONSTRAINT PK_rol_permiso PRIMARY KEY (rol_id, permiso_id)
);
GO

-- Usuarios globales: el rol se asigna por empresa en usuario_empresa
CREATE TABLE dbo.usuarios (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    nombre              NVARCHAR(150) NOT NULL,
    correo              NVARCHAR(200) NOT NULL UNIQUE,
    password_hash       NVARCHAR(255) NOT NULL,          -- nunca la contraseña en texto plano
    totp_secreto        NVARCHAR(255) NULL,              -- secreto 2FA (cifrado por la aplicación)
    es_superadmin       BIT NOT NULL DEFAULT 0,          -- proveedor del sistema, sin empresa
    intentos_fallidos   INT NOT NULL DEFAULT 0,
    bloqueado_hasta     DATETIME2(0) NULL,
    activo              BIT NOT NULL DEFAULT 1,
    ultimo_acceso       DATETIME2(0) NULL,
    creado              DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Un usuario puede pertenecer a varias empresas, con un rol en cada una
CREATE TABLE dbo.usuario_empresa (
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id),
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    rol_id          INT NOT NULL REFERENCES dbo.roles(id),
    activo          BIT NOT NULL DEFAULT 1,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_usuario_empresa PRIMARY KEY (usuario_id, empresa_id)
);
GO

-- Refresh tokens rotativos (solo el hash). empresa_id NULL = sesión del superadministrador
CREATE TABLE dbo.refresh_tokens (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id) ON DELETE CASCADE,
    empresa_id      INT NULL REFERENCES dbo.empresas(id),
    token_hash      NVARCHAR(255) NOT NULL UNIQUE,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    expira          DATETIME2(0) NOT NULL,
    revocado        DATETIME2(0) NULL,
    reemplazado_por BIGINT NULL REFERENCES dbo.refresh_tokens(id),
    ip_origen       VARCHAR(45) NULL
);
GO

-- Enlaces de "recuperar contraseña" (solo el hash; un solo uso, vencen en minutos).
-- Sin empresa: se usan antes de iniciar sesión.
CREATE TABLE dbo.tokens_restablecimiento (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id) ON DELETE CASCADE,
    token_hash      NVARCHAR(255) NOT NULL UNIQUE,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    expira          DATETIME2(0) NOT NULL,
    usado           DATETIME2(0) NULL,
    ip_origen       VARCHAR(45) NULL
);
GO

-- Acceso temporal del superadministrador a una empresa, autorizado por ella
CREATE TABLE dbo.accesos_soporte (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    superadmin_id   INT NOT NULL REFERENCES dbo.usuarios(id),
    autorizado_por  INT NOT NULL REFERENCES dbo.usuarios(id),
    desde           DATETIME2(0) NOT NULL,
    hasta           DATETIME2(0) NOT NULL,
    motivo          NVARCHAR(500) NOT NULL,
    revocado        DATETIME2(0) NULL,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CHECK (hasta > desde)
);
GO


/* =====================================================================
   3. ORGANIZACIÓN E INVENTARIO
   ===================================================================== */

CREATE TABLE dbo.sucursales (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    nombre          NVARCHAR(100) NOT NULL,
    provincia       NVARCHAR(100) NULL,
    direccion       NVARCHAR(300) NULL,
    responsable_id  INT NULL REFERENCES dbo.usuarios(id),
    activa          BIT NOT NULL DEFAULT 1,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_sucursales_empresa_nombre UNIQUE (empresa_id, nombre),
    CONSTRAINT UQ_sucursales_empresa_id UNIQUE (empresa_id, id)
);
GO

-- Sucursales que cubre cada técnico (muchos a muchos)
CREATE TABLE dbo.usuario_sucursal (
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id) ON DELETE CASCADE,
    sucursal_id     INT NOT NULL,
    CONSTRAINT PK_usuario_sucursal PRIMARY KEY (usuario_id, sucursal_id),
    CONSTRAINT FK_usuario_sucursal_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.departamentos (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    nombre          NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_departamentos_empresa_nombre UNIQUE (empresa_id, nombre),
    CONSTRAINT UQ_departamentos_empresa_id UNIQUE (empresa_id, id)
);
GO

CREATE TABLE dbo.maquinas (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id         INT NOT NULL,
    departamento_id     INT NULL,
    codigo_qr           UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() UNIQUE,
    nombre              NVARCHAR(100) NOT NULL,
    tipo                VARCHAR(20) NOT NULL DEFAULT 'computadora'
                        CHECK (tipo IN ('computadora','laptop','servidor','impresora','otro')),
    marca               NVARCHAR(100) NULL,
    modelo              NVARCHAR(100) NULL,
    serie               NVARCHAR(100) NULL,
    sistema_operativo   NVARCHAR(150) NULL,
    ip                  VARCHAR(45)   NULL,
    mac                 VARCHAR(17)   NULL,           -- MAC principal (formato AA:BB:CC:DD:EE:FF)
    responsable         NVARCHAR(150) NULL,
    fecha_compra        DATE NULL,
    garantia_hasta      DATE NULL,
    estado              VARCHAR(20) NOT NULL DEFAULT 'desconectado'
                        CHECK (estado IN ('sano','advertencia','critico','desconectado','baja')),
    ultimo_reporte      DATETIME2(0) NULL,
    ram_total_mb        INT NULL,                     -- del inventario de hardware (para mostrar "usado / total")
    procesos_total      INT NULL,                     -- procesos en ejecución en la última muestra
    creado              DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_maquinas_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_maquinas_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id),
    CONSTRAINT FK_maquinas_departamento FOREIGN KEY (empresa_id, departamento_id)
        REFERENCES dbo.departamentos(empresa_id, id)
);
GO

CREATE TABLE dbo.componentes (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    tipo            VARCHAR(30) NOT NULL,             -- cpu, ram, disco, gpu, red, bateria...
    descripcion     NVARCHAR(200) NULL,
    capacidad       NVARCHAR(50)  NULL,               -- ej. '16 GB', '512 GB SSD'
    estado_salud    VARCHAR(20) NOT NULL DEFAULT 'sano'
                    CHECK (estado_salud IN ('sano','advertencia','critico','desconocido')),
    detalle         NVARCHAR(200) NULL,               -- ej. '148 sectores dañados', 'desgaste 12 %'
    actualizado     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_componentes_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.software_instalado (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id          INT NOT NULL,
    nombre              NVARCHAR(250) NOT NULL,
    version             NVARCHAR(100) NULL,
    editor              NVARCHAR(200) NULL,
    fecha_instalacion   DATE NULL,
    CONSTRAINT FK_software_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

-- Tarjetas de red que reporta el agente (base del cruce por MAC con los switches)
CREATE TABLE dbo.interfaces_red (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    nombre          NVARCHAR(150) NOT NULL,           -- ej. 'Ethernet', 'Wi-Fi'
    mac             VARCHAR(17) NOT NULL,
    ip              VARCHAR(45) NULL,
    tipo            VARCHAR(20) NOT NULL DEFAULT 'cableada'
                    CHECK (tipo IN ('cableada','wifi','virtual','otra')),
    velocidad_mbps  INT NULL,
    actualizado     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_interfaces_red_empresa_mac UNIQUE (empresa_id, mac),
    CONSTRAINT FK_interfaces_red_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

-- Unidades de disco con su espacio ("Disco C: 437 / 476 GB" en la ficha). Las actualiza el agente cada 10 min.
CREATE TABLE dbo.volumenes (
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    unidad          VARCHAR(10) NOT NULL,             -- ej. 'C:'
    etiqueta        NVARCHAR(100) NULL,
    total_gb        DECIMAL(10,1) NOT NULL,
    libre_gb        DECIMAL(10,1) NOT NULL,
    actualizado     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_volumenes PRIMARY KEY (empresa_id, maquina_id, unidad),
    CONSTRAINT FK_volumenes_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.agentes (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL UNIQUE,
    version         VARCHAR(30) NOT NULL,
    token_hash      NVARCHAR(255) NOT NULL,           -- credencial permanente del agente (hash)
    es_recolector   BIT NOT NULL DEFAULT 0,           -- consulta los switches de su sucursal por SNMP
    ultimo_contacto DATETIME2(0) NULL,
    revocado        BIT NOT NULL DEFAULT 0,
    aprobado        BIT NOT NULL DEFAULT 1,           -- 0 = pendiente de aprobación (W-19): no puede conectarse
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_agentes_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_agentes_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.tokens_registro (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id     INT NOT NULL,
    token_hash      NVARCHAR(255) NOT NULL UNIQUE,
    vence           DATETIME2(0) NOT NULL,
    nombre          NVARCHAR(100) NULL,               -- etiqueta para reconocerlo en el panel
    usos_max        INT NULL,                         -- NULL = sin límite
    usos            INT NOT NULL DEFAULT 0,
    requiere_aprobacion BIT NOT NULL DEFAULT 0,       -- los equipos registrados quedan pendientes (W-19)
    revocado        DATETIME2(0) NULL,                -- revocado desde el panel: ya no registra equipos
    creado_por      INT NOT NULL REFERENCES dbo.usuarios(id),
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CHECK (usos_max IS NULL OR usos <= usos_max),
    CONSTRAINT FK_tokens_registro_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id) ON DELETE CASCADE
);
GO


/* =====================================================================
   4. RED Y DATA CENTER
   ===================================================================== */

CREATE TABLE dbo.salas (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id     INT NOT NULL,
    nombre          NVARCHAR(100) NOT NULL,           -- ej. 'Data Center Principal'
    ubicacion       NVARCHAR(200) NULL,
    CONSTRAINT UQ_salas_sucursal_nombre UNIQUE (empresa_id, sucursal_id, nombre),
    CONSTRAINT UQ_salas_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_salas_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id)
);
GO

CREATE TABLE dbo.gabinetes (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    sala_id         INT NOT NULL,
    nombre          NVARCHAR(50) NOT NULL,            -- ej. 'GAB-A01'
    codigo_qr       UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() UNIQUE,
    altura_u        TINYINT NOT NULL DEFAULT 42 CHECK (altura_u BETWEEN 1 AND 60),
    fila            NVARCHAR(10) NULL,                -- posición en el plano de la sala
    posicion        INT NULL,
    activo          BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_gabinetes_sala_nombre UNIQUE (empresa_id, sala_id, nombre),
    CONSTRAINT UQ_gabinetes_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_gabinetes_sala FOREIGN KEY (empresa_id, sala_id)
        REFERENCES dbo.salas(empresa_id, id)
);
GO

-- Catálogo: cada modelo se registra una vez y sus puertos se crean al agregar un dispositivo.
-- empresa_id NULL = catálogo base compartido; con valor = modelo propio de esa empresa.
CREATE TABLE dbo.modelos_dispositivo (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NULL REFERENCES dbo.empresas(id),
    marca               NVARCHAR(100) NOT NULL,
    modelo              NVARCHAR(100) NOT NULL,
    tipo                VARCHAR(20) NOT NULL
                        CHECK (tipo IN ('switch','router','firewall','servidor','patch_panel','ups',
                                        'pdu','bandeja','organizador','access_point','otro')),
    altura_u            TINYINT NOT NULL DEFAULT 1 CHECK (altura_u BETWEEN 0 AND 20),
    cantidad_puertos    INT NOT NULL DEFAULT 0 CHECK (cantidad_puertos >= 0),
    tipo_puertos        NVARCHAR(100) NULL,           -- ej. '48 RJ45 + 4 SFP+'
    es_activo           BIT NOT NULL DEFAULT 1,       -- 1 = tiene SNMP; 0 = pasivo (patch panel)
    CONSTRAINT UQ_modelos_marca_modelo UNIQUE (empresa_id, marca, modelo)
);
GO

CREATE TABLE dbo.dispositivos_red (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id              INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id             INT NOT NULL,             -- la del gabinete si va en uno; define el alcance y el recolector
    gabinete_id             INT NULL,                 -- NULL si no va en gabinete (ej. AP)
    modelo_id               INT NOT NULL REFERENCES dbo.modelos_dispositivo(id),
    recolector_id           INT NULL,                 -- agente que lo consulta por SNMP
    nombre                  NVARCHAR(100) NOT NULL,   -- ej. 'SW-ACC-02'
    tipo                    VARCHAR(20) NOT NULL
                            CHECK (tipo IN ('switch','router','firewall','servidor','patch_panel','ups',
                                            'pdu','bandeja','organizador','access_point','otro')),
    ip_gestion              VARCHAR(45) NULL,
    posicion_u              TINYINT NULL,             -- U inferior que ocupa en el gabinete
    altura_u                TINYINT NOT NULL DEFAULT 1,
    codigo_qr               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() UNIQUE,
    snmp_usuario            NVARCHAR(100) NULL,       -- SNMPv3 de solo lectura
    snmp_credencial_cifrada VARBINARY(MAX) NULL,      -- cifrada por la aplicación, nunca en texto plano
    firmware                NVARCHAR(100) NULL,
    estado                  VARCHAR(20) NOT NULL DEFAULT 'sin_respuesta'
                            CHECK (estado IN ('en_linea','advertencia','critico','sin_respuesta','pasivo','baja')),
    ultimo_sondeo           DATETIME2(0) NULL,
    creado                  DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CHECK (gabinete_id IS NULL OR posicion_u IS NOT NULL),
    CONSTRAINT UQ_dispositivos_red_empresa_nombre UNIQUE (empresa_id, nombre),
    CONSTRAINT UQ_dispositivos_red_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_dispositivos_red_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id),
    CONSTRAINT FK_dispositivos_red_gabinete FOREIGN KEY (empresa_id, gabinete_id)
        REFERENCES dbo.gabinetes(empresa_id, id),
    CONSTRAINT FK_dispositivos_red_recolector FOREIGN KEY (empresa_id, recolector_id)
        REFERENCES dbo.agentes(empresa_id, id)
);
GO

CREATE TABLE dbo.puertos (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    dispositivo_id  INT NOT NULL,
    numero          INT NOT NULL,
    nombre          NVARCHAR(50) NULL,                -- ej. 'Gi1/0/12'
    tipo            VARCHAR(20) NOT NULL DEFAULT 'rj45'
                    CHECK (tipo IN ('rj45','sfp','sfp_plus','fibra','consola','otro')),
    vlan            INT NULL,
    velocidad_mbps  INT NULL,
    duplex          VARCHAR(10) NULL CHECK (duplex IN ('full','half','auto')),
    estado          VARCHAR(20) NOT NULL DEFAULT 'libre'
                    CHECK (estado IN ('conectado','libre','errores','caido','deshabilitado')),
    es_troncal      BIT NOT NULL DEFAULT 0,           -- uplink a otro switch: se ignora en el cruce MAC
    es_host_vm      BIT NOT NULL DEFAULT 0,           -- servidor con máquinas virtuales
    asignacion_manual BIT NOT NULL DEFAULT 0,         -- un técnico fijó a mano lo conectado
    CONSTRAINT UQ_puertos_dispositivo_numero UNIQUE (empresa_id, dispositivo_id, numero),
    CONSTRAINT UQ_puertos_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_puertos_dispositivo FOREIGN KEY (empresa_id, dispositivo_id)
        REFERENCES dbo.dispositivos_red(empresa_id, id) ON DELETE CASCADE
);
GO

-- Lo que el switch ve en cada puerto; maquina_id se llena al cruzar con interfaces_red.mac
CREATE TABLE dbo.tabla_mac (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    puerto_id       INT NOT NULL,
    maquina_id      INT NULL REFERENCES dbo.maquinas(id) ON DELETE SET NULL,
    mac             VARCHAR(17) NOT NULL,
    ip              VARCHAR(45) NULL,
    vlan            INT NULL,
    fabricante      NVARCHAR(150) NULL,
    primera_vez     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    ultima_vez      DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_tabla_mac_puerto_mac UNIQUE (empresa_id, puerto_id, mac),
    CONSTRAINT FK_tabla_mac_puerto FOREIGN KEY (empresa_id, puerto_id)
        REFERENCES dbo.puertos(empresa_id, id) ON DELETE CASCADE
);
GO

-- Cables entre equipos de red detectados por LLDP/CDP o registrados a mano
CREATE TABLE dbo.enlaces_red (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    puerto_origen_id    INT NOT NULL,
    puerto_destino_id   INT NOT NULL,
    protocolo           VARCHAR(10) NOT NULL DEFAULT 'lldp' CHECK (protocolo IN ('lldp','cdp','manual')),
    detectado           DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CHECK (puerto_origen_id <> puerto_destino_id),
    CONSTRAINT UQ_enlaces_red UNIQUE (empresa_id, puerto_origen_id, puerto_destino_id),
    CONSTRAINT FK_enlaces_red_origen FOREIGN KEY (empresa_id, puerto_origen_id)
        REFERENCES dbo.puertos(empresa_id, id),
    CONSTRAINT FK_enlaces_red_destino FOREIGN KEY (empresa_id, puerto_destino_id)
        REFERENCES dbo.puertos(empresa_id, id)
);
GO

CREATE TABLE dbo.metricas_puerto (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    puerto_id           INT NOT NULL,
    fecha_hora          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    bytes_entrada       BIGINT NULL,
    bytes_salida        BIGINT NULL,
    errores_entrada     BIGINT NULL,
    errores_salida      BIGINT NULL,
    descartes           BIGINT NULL,
    CONSTRAINT FK_metricas_puerto_puerto FOREIGN KEY (empresa_id, puerto_id)
        REFERENCES dbo.puertos(empresa_id, id) ON DELETE CASCADE
);
GO

-- Rosetas de pared o escritorio
CREATE TABLE dbo.puntos_red (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id     INT NOT NULL,
    codigo          NVARCHAR(30) NOT NULL,            -- ej. 'P-2-15'
    ubicacion       NVARCHAR(200) NULL,               -- ej. 'Contabilidad, escritorio 3'
    codigo_qr       UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID() UNIQUE,
    CONSTRAINT UQ_puntos_red_sucursal_codigo UNIQUE (empresa_id, sucursal_id, codigo),
    CONSTRAINT UQ_puntos_red_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_puntos_red_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id)
);
GO

-- Cableado documentado: un cable une dos puertos o un puerto con una roseta
CREATE TABLE dbo.cableado (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    puerto_a_id     INT NOT NULL,
    puerto_b_id     INT NULL,
    punto_red_id    INT NULL,
    tipo_cable      VARCHAR(20) NULL CHECK (tipo_cable IN ('cat5e','cat6','cat6a','cat7','fibra_mm','fibra_sm','dac','otro')),
    color           NVARCHAR(30) NULL,
    etiqueta        NVARCHAR(50) NULL,
    notas           NVARCHAR(500) NULL,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    -- exactamente un destino: otro puerto o una roseta
    CHECK ( (puerto_b_id IS NOT NULL AND punto_red_id IS NULL)
         OR (puerto_b_id IS NULL     AND punto_red_id IS NOT NULL) ),
    CHECK (puerto_b_id IS NULL OR puerto_a_id <> puerto_b_id),
    CONSTRAINT FK_cableado_puerto_a FOREIGN KEY (empresa_id, puerto_a_id)
        REFERENCES dbo.puertos(empresa_id, id),
    CONSTRAINT FK_cableado_puerto_b FOREIGN KEY (empresa_id, puerto_b_id)
        REFERENCES dbo.puertos(empresa_id, id),
    CONSTRAINT FK_cableado_punto_red FOREIGN KEY (empresa_id, punto_red_id)
        REFERENCES dbo.puntos_red(empresa_id, id)
);
GO


/* =====================================================================
   5. MONITOREO Y ALERTAS
   ===================================================================== */

-- Detalle de los últimos 30 días (ver sp_resumir_y_purgar_metricas)
CREATE TABLE dbo.metricas (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    fecha_hora      DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    cpu             DECIMAL(5,2) NULL,                -- porcentaje
    ram             DECIMAL(5,2) NULL,                -- porcentaje
    disco           DECIMAL(5,2) NULL,                -- porcentaje del disco más lleno
    temperatura     DECIMAL(5,2) NULL,                -- °C
    CONSTRAINT FK_metricas_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

-- Foto diaria del semáforo de la flota (gráfica "Salud de la flota · últimos 7 días").
-- La guarda el servicio de presencia a lo largo del día; el último valor del día queda como su resumen.
CREATE TABLE dbo.estado_diario (
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    fecha           DATE NOT NULL,
    sano            INT NOT NULL DEFAULT 0,
    advertencia     INT NOT NULL DEFAULT 0,
    critico         INT NOT NULL DEFAULT 0,
    desconectado    INT NOT NULL DEFAULT 0,
    actualizado     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_estado_diario PRIMARY KEY (empresa_id, fecha)
);
GO

-- Resumen histórico por hora (se guarda 1 año)
CREATE TABLE dbo.metricas_hora (
    id                  BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id          INT NOT NULL,
    hora                DATETIME2(0) NOT NULL,
    cpu_promedio        DECIMAL(5,2) NULL,
    ram_promedio        DECIMAL(5,2) NULL,
    disco_max           DECIMAL(5,2) NULL,
    temperatura_max     DECIMAL(5,2) NULL,
    CONSTRAINT UQ_metricas_hora UNIQUE (empresa_id, maquina_id, hora),
    CONSTRAINT FK_metricas_hora_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.eventos_error (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    fecha_hora      DATETIME2(0) NOT NULL,
    origen          NVARCHAR(200) NULL,               -- ej. 'Microsoft-Windows-Kernel-Power'
    codigo          INT NULL,                         -- ej. 41
    nivel           VARCHAR(15) NOT NULL CHECK (nivel IN ('info','advertencia','error','critico')),
    mensaje         NVARCHAR(MAX) NULL,
    mensaje_simple  NVARCHAR(500) NULL,               -- explicación en lenguaje simple
    CONSTRAINT FK_eventos_error_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.procesos_snapshot (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    fecha_hora      DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    nombre          NVARCHAR(260) NOT NULL,
    pid             INT NULL,
    cpu             DECIMAL(5,2) NULL,                -- porcentaje
    ram_mb          DECIMAL(10,2) NULL,
    CONSTRAINT FK_procesos_snapshot_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

-- Servicios de Windows con inicio automático. detenido_desde se marca cuando uno que estaba en ejecución
-- se detiene (regla "servicio automático detenido"); se borra al volver a ejecutarse.
CREATE TABLE dbo.servicios (
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    nombre          NVARCHAR(256) NOT NULL,
    nombre_visible  NVARCHAR(256) NULL,
    estado          VARCHAR(20) NOT NULL CHECK (estado IN ('en_ejecucion','detenido','otro')),
    inicio          VARCHAR(25) NOT NULL CHECK (inicio IN ('automatico','automatico_retrasado','manual','deshabilitado')),
    detenido_desde  DATETIME2(0) NULL,
    actualizado     DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_servicios PRIMARY KEY (empresa_id, maquina_id, nombre),
    CONSTRAINT FK_servicios_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.reglas_alerta (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    sucursal_id         INT NULL,                     -- NULL = aplica a toda la empresa
    nombre              NVARCHAR(100) NOT NULL,
    metrica             VARCHAR(30) NOT NULL
                        CHECK (metrica IN ('cpu','ram','disco','temperatura','sin_reportar',
                                           'errores_criticos','servicio_detenido','toner',
                                           'antivirus','puerto_errores','puerto_caido','switch_sin_respuesta')),
    operador            VARCHAR(2) NOT NULL DEFAULT '>' CHECK (operador IN ('>','>=','<','<=','=')),
    umbral_advertencia  DECIMAL(10,2) NULL,
    umbral_critico      DECIMAL(10,2) NULL,
    duracion_min        INT NOT NULL DEFAULT 0,       -- minutos que debe sostenerse
    activa              BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_reglas_alerta_sucursal FOREIGN KEY (empresa_id, sucursal_id)
        REFERENCES dbo.sucursales(empresa_id, id)
);
GO

-- Una alerta es de una máquina, de un dispositivo de red o de un puerto
CREATE TABLE dbo.alertas (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NULL,
    dispositivo_id  INT NULL,
    puerto_id       INT NULL,
    regla_id        INT NULL REFERENCES dbo.reglas_alerta(id) ON DELETE SET NULL,
    atendida_por    INT NULL REFERENCES dbo.usuarios(id),
    nivel           VARCHAR(15) NOT NULL CHECK (nivel IN ('advertencia','critico')),
    estado          VARCHAR(15) NOT NULL DEFAULT 'nueva' CHECK (estado IN ('nueva','en_proceso','resuelta')),
    mensaje         NVARCHAR(500) NOT NULL,
    codigo          VARCHAR(80) NULL,                 -- tipo de problema y su clave (ej. 'disco_lleno:C:'): una alerta abierta por código
    detalle         NVARCHAR(500) NULL,               -- explicación en lenguaje simple
    que_hacer       NVARCHAR(500) NULL,               -- recomendación para el técnico
    origen          NVARCHAR(200) NULL,               -- de dónde sale (ej. 'SMART 5 · Reallocated Sector Count')
    creada          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    resuelta        DATETIME2(0) NULL,
    CHECK (maquina_id IS NOT NULL OR dispositivo_id IS NOT NULL OR puerto_id IS NOT NULL),
    CONSTRAINT UQ_alertas_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_alertas_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id),
    CONSTRAINT FK_alertas_dispositivo FOREIGN KEY (empresa_id, dispositivo_id)
        REFERENCES dbo.dispositivos_red(empresa_id, id),
    CONSTRAINT FK_alertas_puerto FOREIGN KEY (empresa_id, puerto_id)
        REFERENCES dbo.puertos(empresa_id, id)
);
GO


/* =====================================================================
   6. SOPORTE Y GESTIÓN REMOTA
   ===================================================================== */

CREATE TABLE dbo.tickets (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NULL,
    dispositivo_id  INT NULL,
    alerta_id       BIGINT NULL,
    creado_por      INT NOT NULL REFERENCES dbo.usuarios(id),
    asignado_a      INT NULL REFERENCES dbo.usuarios(id),
    titulo          NVARCHAR(200) NOT NULL,
    descripcion     NVARCHAR(MAX) NULL,
    prioridad       VARCHAR(10) NOT NULL DEFAULT 'media' CHECK (prioridad IN ('baja','media','alta','urgente')),
    estado          VARCHAR(15) NOT NULL DEFAULT 'abierto'
                    CHECK (estado IN ('abierto','en_proceso','en_espera','resuelto','cerrado')),
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    cerrado         DATETIME2(0) NULL,
    CONSTRAINT UQ_tickets_empresa_id UNIQUE (empresa_id, id),
    CONSTRAINT FK_tickets_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id),
    CONSTRAINT FK_tickets_dispositivo FOREIGN KEY (empresa_id, dispositivo_id)
        REFERENCES dbo.dispositivos_red(empresa_id, id),
    CONSTRAINT FK_tickets_alerta FOREIGN KEY (empresa_id, alerta_id)
        REFERENCES dbo.alertas(empresa_id, id)
);
GO

CREATE TABLE dbo.ticket_comentarios (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    ticket_id       INT NOT NULL,
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id),
    texto           NVARCHAR(MAX) NOT NULL,
    fecha           DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_ticket_comentarios_ticket FOREIGN KEY (empresa_id, ticket_id)
        REFERENCES dbo.tickets(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.ticket_adjuntos (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    ticket_id       INT NOT NULL,
    archivo_url     NVARCHAR(500) NOT NULL,
    tipo            VARCHAR(50) NULL,                 -- ej. 'image/jpeg'
    fecha           DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_ticket_adjuntos_ticket FOREIGN KEY (empresa_id, ticket_id)
        REFERENCES dbo.tickets(empresa_id, id) ON DELETE CASCADE
);
GO

CREATE TABLE dbo.mantenimientos (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id          INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id          INT NOT NULL,
    tecnico_id          INT NULL REFERENCES dbo.usuarios(id),
    tipo                VARCHAR(15) NOT NULL CHECK (tipo IN ('preventivo','correctivo')),
    descripcion         NVARCHAR(500) NULL,
    fecha_programada    DATE NULL,
    fecha_realizada     DATE NULL,
    CONSTRAINT FK_mantenimientos_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id)
);
GO

CREATE TABLE dbo.scripts (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    creado_por      INT NOT NULL REFERENCES dbo.usuarios(id),
    nombre          NVARCHAR(150) NOT NULL,
    lenguaje        VARCHAR(15) NOT NULL CHECK (lenguaje IN ('powershell','cmd','bash','python')),
    contenido       NVARCHAR(MAX) NOT NULL,
    firma           NVARCHAR(1000) NULL,              -- firma del servidor; el agente rechaza lo no firmado
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_scripts_empresa_nombre UNIQUE (empresa_id, nombre)
);
GO

CREATE TABLE dbo.comandos (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id),
    script_id       INT NULL REFERENCES dbo.scripts(id) ON DELETE SET NULL,
    tipo            VARCHAR(15) NOT NULL CHECK (tipo IN ('comando','script','accion')),
    contenido       NVARCHAR(MAX) NULL,
    estado          VARCHAR(15) NOT NULL DEFAULT 'pendiente'
                    CHECK (estado IN ('pendiente','enviado','ejecutando','completado','error','expirado')),
    resultado       NVARCHAR(MAX) NULL,
    creado          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    terminado       DATETIME2(0) NULL,
    CONSTRAINT FK_comandos_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id)
);
GO

CREATE TABLE dbo.sesiones_remotas (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NOT NULL REFERENCES dbo.empresas(id),
    maquina_id      INT NOT NULL,
    usuario_id      INT NOT NULL REFERENCES dbo.usuarios(id),
    tipo            VARCHAR(15) NOT NULL CHECK (tipo IN ('escritorio','terminal','archivos')),
    inicio          DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    fin             DATETIME2(0) NULL,
    ip_origen       VARCHAR(45) NULL,
    CONSTRAINT FK_sesiones_remotas_maquina FOREIGN KEY (empresa_id, maquina_id)
        REFERENCES dbo.maquinas(empresa_id, id)
);
GO

-- empresa_id NULL = acción del superadministrador o del sistema fuera de una empresa
CREATE TABLE dbo.auditoria (
    id              BIGINT IDENTITY(1,1) PRIMARY KEY,
    empresa_id      INT NULL REFERENCES dbo.empresas(id),
    usuario_id      INT NULL REFERENCES dbo.usuarios(id),   -- NULL = acción del sistema
    fecha           DATETIME2(0) NOT NULL DEFAULT SYSUTCDATETIME(),
    accion          VARCHAR(50) NOT NULL,             -- ej. 'crear', 'editar', 'eliminar', 'conectar'
    entidad         VARCHAR(50) NOT NULL,             -- ej. 'maquinas', 'gabinetes'
    entidad_id      NVARCHAR(50) NULL,
    detalle         NVARCHAR(MAX) NULL,               -- JSON con valores antes/después
    ip_origen       VARCHAR(45) NULL
);
GO


/* =====================================================================
   7. ÍNDICES
   Todos los índices de tablas de negocio empiezan por empresa_id.
   ===================================================================== */

-- Empresas y seguridad
CREATE UNIQUE INDEX UX_empresas_rnc         ON dbo.empresas (rnc) WHERE rnc IS NOT NULL;
CREATE INDEX IX_suscripciones_empresa       ON dbo.suscripciones (empresa_id, estado);
CREATE UNIQUE INDEX UX_empresa_llaves_activa ON dbo.empresa_llaves (empresa_id) WHERE activa = 1;
CREATE INDEX IX_usuario_empresa_empresa     ON dbo.usuario_empresa (empresa_id, rol_id);
CREATE INDEX IX_refresh_tokens_usuario      ON dbo.refresh_tokens (usuario_id, expira);
CREATE INDEX IX_accesos_soporte_empresa     ON dbo.accesos_soporte (empresa_id, hasta);
CREATE INDEX IX_usuario_sucursal_sucursal   ON dbo.usuario_sucursal (empresa_id, sucursal_id);

-- Inventario
CREATE INDEX IX_maquinas_sucursal        ON dbo.maquinas (empresa_id, sucursal_id, estado);
CREATE INDEX IX_maquinas_estado          ON dbo.maquinas (empresa_id, estado) INCLUDE (nombre, sucursal_id, ultimo_reporte);
CREATE INDEX IX_componentes_maquina      ON dbo.componentes (empresa_id, maquina_id);
CREATE INDEX IX_software_maquina         ON dbo.software_instalado (empresa_id, maquina_id);
CREATE INDEX IX_interfaces_maquina       ON dbo.interfaces_red (empresa_id, maquina_id);
CREATE INDEX IX_agentes_contacto         ON dbo.agentes (empresa_id, ultimo_contacto);
CREATE INDEX IX_tokens_registro_sucursal ON dbo.tokens_registro (empresa_id, sucursal_id);

-- Monitoreo (las consultas siempre filtran por máquina y rango de fechas)
CREATE INDEX IX_metricas_maquina_fecha   ON dbo.metricas (empresa_id, maquina_id, fecha_hora)
    INCLUDE (cpu, ram, disco, temperatura);
CREATE INDEX IX_metricas_fecha           ON dbo.metricas (empresa_id, fecha_hora);
CREATE INDEX IX_metricas_hora_hora       ON dbo.metricas_hora (empresa_id, hora);
CREATE INDEX IX_eventos_maquina_fecha    ON dbo.eventos_error (empresa_id, maquina_id, fecha_hora DESC) INCLUDE (nivel);
CREATE INDEX IX_eventos_fecha            ON dbo.eventos_error (empresa_id, fecha_hora);
CREATE INDEX IX_procesos_maquina_fecha   ON dbo.procesos_snapshot (empresa_id, maquina_id, fecha_hora DESC);
CREATE INDEX IX_procesos_fecha           ON dbo.procesos_snapshot (empresa_id, fecha_hora);
CREATE INDEX IX_reglas_alerta_empresa    ON dbo.reglas_alerta (empresa_id, metrica) WHERE activa = 1;
CREATE INDEX IX_alertas_estado           ON dbo.alertas (empresa_id, estado, nivel, creada DESC);
CREATE INDEX IX_alertas_maquina          ON dbo.alertas (empresa_id, maquina_id) WHERE maquina_id IS NOT NULL;
-- El motor de alertas busca la alerta abierta de cada problema por su código.
CREATE INDEX IX_alertas_codigo           ON dbo.alertas (empresa_id, maquina_id, codigo) WHERE estado <> 'resuelta';
CREATE INDEX IX_alertas_dispositivo      ON dbo.alertas (empresa_id, dispositivo_id) WHERE dispositivo_id IS NOT NULL;

-- Red
CREATE INDEX IX_dispositivos_gabinete    ON dbo.dispositivos_red (empresa_id, gabinete_id, posicion_u);
CREATE INDEX IX_dispositivos_recolector  ON dbo.dispositivos_red (empresa_id, recolector_id);
CREATE INDEX IX_tabla_mac_mac            ON dbo.tabla_mac (empresa_id, mac) INCLUDE (puerto_id, ip, ultima_vez);
CREATE INDEX IX_tabla_mac_maquina        ON dbo.tabla_mac (empresa_id, maquina_id) WHERE maquina_id IS NOT NULL;
CREATE INDEX IX_metricas_puerto_fecha    ON dbo.metricas_puerto (empresa_id, puerto_id, fecha_hora);
CREATE INDEX IX_metricas_puerto_purga    ON dbo.metricas_puerto (empresa_id, fecha_hora);
CREATE INDEX IX_cableado_puerto_a        ON dbo.cableado (empresa_id, puerto_a_id);
CREATE INDEX IX_cableado_puerto_b        ON dbo.cableado (empresa_id, puerto_b_id) WHERE puerto_b_id IS NOT NULL;
CREATE INDEX IX_cableado_punto_red       ON dbo.cableado (empresa_id, punto_red_id) WHERE punto_red_id IS NOT NULL;

-- Soporte y auditoría
CREATE INDEX IX_tickets_estado           ON dbo.tickets (empresa_id, estado, prioridad) INCLUDE (asignado_a, maquina_id);
CREATE INDEX IX_tickets_asignado         ON dbo.tickets (empresa_id, asignado_a, estado);
CREATE INDEX IX_mantenimientos_maquina   ON dbo.mantenimientos (empresa_id, maquina_id, fecha_programada);
CREATE INDEX IX_comandos_maquina_estado  ON dbo.comandos (empresa_id, maquina_id, estado);
CREATE INDEX IX_sesiones_maquina         ON dbo.sesiones_remotas (empresa_id, maquina_id, inicio DESC);
CREATE INDEX IX_auditoria_fecha          ON dbo.auditoria (empresa_id, fecha DESC);
CREATE INDEX IX_auditoria_entidad        ON dbo.auditoria (empresa_id, entidad, entidad_id);
GO


/* =====================================================================
   8. VISTA: ¿EN QUÉ SWITCH Y PUERTO ESTÁ CADA MÁQUINA?
   Toma la última detección de cada máquina en un puerto de acceso
   (ignora puertos troncales). Con RLS activo solo devuelve filas de la
   empresa del contexto de sesión.
   ===================================================================== */
CREATE VIEW dbo.vw_ubicacion_red_maquina
AS
WITH detecciones AS (
    SELECT
        tm.empresa_id,
        tm.maquina_id,
        tm.mac,
        tm.ip,
        tm.vlan,
        tm.ultima_vez,
        p.id            AS puerto_id,
        p.numero        AS puerto_numero,
        p.nombre        AS puerto_nombre,
        d.id            AS dispositivo_id,
        d.nombre        AS switch_nombre,
        g.nombre        AS gabinete,
        s.nombre        AS sala,
        su.nombre       AS sucursal,
        ROW_NUMBER() OVER (PARTITION BY tm.empresa_id, tm.maquina_id ORDER BY tm.ultima_vez DESC) AS orden
    FROM dbo.tabla_mac tm
    JOIN dbo.puertos p           ON p.empresa_id = tm.empresa_id AND p.id = tm.puerto_id AND p.es_troncal = 0
    JOIN dbo.dispositivos_red d  ON d.empresa_id = p.empresa_id AND d.id = p.dispositivo_id
    LEFT JOIN dbo.gabinetes g    ON g.empresa_id = d.empresa_id AND g.id = d.gabinete_id
    LEFT JOIN dbo.salas s        ON s.empresa_id = g.empresa_id AND s.id = g.sala_id
    LEFT JOIN dbo.sucursales su  ON su.empresa_id = s.empresa_id AND su.id = s.sucursal_id
    WHERE tm.maquina_id IS NOT NULL
)
SELECT
    m.empresa_id,
    m.id            AS maquina_id,
    m.nombre        AS maquina,
    det.mac,
    det.ip,
    det.vlan,
    det.sucursal,
    det.sala,
    det.gabinete,
    det.dispositivo_id,
    det.switch_nombre,
    det.puerto_id,
    det.puerto_numero,
    det.puerto_nombre,
    det.ultima_vez
FROM dbo.maquinas m
JOIN detecciones det ON det.empresa_id = m.empresa_id AND det.maquina_id = m.id AND det.orden = 1;
GO


/* =====================================================================
   9. PROCEDIMIENTO: RESUMEN Y LIMPIEZA DE MÉTRICAS
   Lo ejecuta el servidor cada hora, una vez por empresa, con
   SESSION_CONTEXT(N'empresa_id') ya fijado: RLS limita cada ejecución
   a esa empresa y @empresa_id debe coincidir con el contexto.
   - Resume las horas completas de 'metricas' en 'metricas_hora'.
   - Borra el detalle viejo por lotes (no llena el log de transacciones).
   ===================================================================== */
CREATE PROCEDURE dbo.sp_resumir_y_purgar_metricas
    @empresa_id           INT,
    @dias_detalle         INT = 30,    -- detalle minuto a minuto
    @dias_resumen         INT = 365,   -- resumen por hora
    @dias_procesos        INT = 7,     -- fotos de procesos
    @dias_metricas_puerto INT = 30,
    @lote                 INT = 5000
AS
BEGIN
    SET NOCOUNT ON;

    IF @empresa_id IS NULL
       OR @empresa_id <> ISNULL(CAST(SESSION_CONTEXT(N'empresa_id') AS INT), -1)
    BEGIN
        THROW 50001, N'El contexto de sesión no corresponde a la empresa indicada.', 1;
    END;

    DECLARE @ahora      DATETIME2(0) = SYSUTCDATETIME();
    DECLARE @base       DATETIME2(0) = '2000-01-01';
    DECLARE @hora_actual DATETIME2(0) = DATEADD(HOUR, DATEDIFF(HOUR, @base, @ahora), @base);
    DECLARE @filas      INT;

    /* 1. Resumir horas completas que aún no estén en metricas_hora */
    INSERT INTO dbo.metricas_hora (empresa_id, maquina_id, hora, cpu_promedio, ram_promedio, disco_max, temperatura_max)
    SELECT
        m.empresa_id,
        m.maquina_id,
        DATEADD(HOUR, DATEDIFF(HOUR, @base, m.fecha_hora), @base) AS hora,
        CAST(AVG(m.cpu) AS DECIMAL(5,2)),
        CAST(AVG(m.ram) AS DECIMAL(5,2)),
        MAX(m.disco),
        MAX(m.temperatura)
    FROM dbo.metricas m
    WHERE m.empresa_id = @empresa_id
      AND m.fecha_hora < @hora_actual
      AND NOT EXISTS (
            SELECT 1 FROM dbo.metricas_hora h
            WHERE h.empresa_id = m.empresa_id
              AND h.maquina_id = m.maquina_id
              AND h.hora = DATEADD(HOUR, DATEDIFF(HOUR, @base, m.fecha_hora), @base)
      )
    GROUP BY m.empresa_id, m.maquina_id, DATEADD(HOUR, DATEDIFF(HOUR, @base, m.fecha_hora), @base);

    /* 2. Borrar detalle viejo por lotes */
    SET @filas = 1;
    WHILE @filas > 0
    BEGIN
        DELETE TOP (@lote) FROM dbo.metricas
        WHERE empresa_id = @empresa_id AND fecha_hora < DATEADD(DAY, -@dias_detalle, @ahora);
        SET @filas = @@ROWCOUNT;
    END;

    SET @filas = 1;
    WHILE @filas > 0
    BEGIN
        DELETE TOP (@lote) FROM dbo.metricas_hora
        WHERE empresa_id = @empresa_id AND hora < DATEADD(DAY, -@dias_resumen, @ahora);
        SET @filas = @@ROWCOUNT;
    END;

    SET @filas = 1;
    WHILE @filas > 0
    BEGIN
        DELETE TOP (@lote) FROM dbo.procesos_snapshot
        WHERE empresa_id = @empresa_id AND fecha_hora < DATEADD(DAY, -@dias_procesos, @ahora);
        SET @filas = @@ROWCOUNT;
    END;

    SET @filas = 1;
    WHILE @filas > 0
    BEGIN
        DELETE TOP (@lote) FROM dbo.metricas_puerto
        WHERE empresa_id = @empresa_id AND fecha_hora < DATEADD(DAY, -@dias_metricas_puerto, @ahora);
        SET @filas = @@ROWCOUNT;
    END;
END;
GO


/* =====================================================================
   10. DATOS INICIALES: PLANES, ROLES Y PERMISOS
   - Los límites y precios de los planes son valores iniciales; se
     ajustan desde el portal del superadministrador.
   - Los roles se crean como roles del sistema (empresa_id NULL).
   - El primer usuario se crea desde la aplicación, porque la contraseña
     debe guardarse como hash (BCrypt/Argon2).
   ===================================================================== */

INSERT INTO dbo.planes (nombre, max_agentes, max_sucursales, max_usuarios, precio_mensual) VALUES
    (N'Básico',       50,   3,    5,    0),
    (N'Profesional',  250,  15,   25,   0),
    (N'Empresarial',  NULL, NULL, NULL, 0);

INSERT INTO dbo.plan_modulos (plan_id, modulo)
SELECT p.id, m.modulo
FROM dbo.planes p
JOIN (VALUES
    (N'Básico',      'monitoreo'),
    (N'Profesional', 'monitoreo'), (N'Profesional', 'remoto'),
    (N'Empresarial', 'monitoreo'), (N'Empresarial', 'remoto'),
    (N'Empresarial', 'red'),       (N'Empresarial', 'reportes_avanzados')
) AS m (plan_nombre, modulo) ON m.plan_nombre = p.nombre;

INSERT INTO dbo.roles (empresa_id, nombre, descripcion) VALUES
    (NULL, N'Administrador', N'Acceso total al sistema'),
    (NULL, N'Tecnico',       N'Atiende alertas y tickets, se conecta de forma remota'),
    (NULL, N'Red',           N'Administra el data center, switches y cableado'),
    (NULL, N'Supervisor',    N'Consulta dashboard y reportes, sin cambiar configuración'),
    (NULL, N'Empleado',      N'Escanea su equipo y reporta fallas');

INSERT INTO dbo.permisos (clave, descripcion) VALUES
    (N'dashboard.ver',          N'Ver el dashboard general'),
    (N'equipos.ver',            N'Ver máquinas y su estado'),
    (N'equipos.editar',         N'Crear, editar y dar de baja máquinas'),
    (N'sucursales.editar',      N'Crear y editar sucursales'),
    (N'instaladores.generar',   N'Generar tokens e instaladores del agente'),
    (N'alertas.ver',            N'Ver alertas'),
    (N'alertas.gestionar',      N'Atender alertas y editar reglas'),
    (N'tickets.crear',          N'Crear tickets'),
    (N'tickets.gestionar',      N'Asignar, cambiar estado y cerrar tickets'),
    (N'remoto.conectar',        N'Conectarse al escritorio remoto'),
    (N'remoto.terminal',        N'Usar la terminal remota'),
    (N'remoto.archivos',        N'Usar el explorador de archivos remoto'),
    (N'scripts.ejecutar',       N'Ejecutar comandos y scripts'),
    (N'scripts.editar',         N'Crear y editar la biblioteca de scripts'),
    (N'red.ver',                N'Ver data center, switches y puertos'),
    (N'red.editar',             N'Administrar gabinetes, dispositivos y cableado'),
    (N'reportes.ver',           N'Ver y exportar reportes'),
    (N'usuarios.administrar',   N'Administrar usuarios, roles y permisos'),
    (N'auditoria.ver',          N'Ver la bitácora de auditoría'),
    (N'configuracion.editar',   N'Cambiar la configuración general');

-- Administrador: todos los permisos
INSERT INTO dbo.rol_permiso (rol_id, permiso_id)
SELECT r.id, p.id
FROM dbo.roles r CROSS JOIN dbo.permisos p
WHERE r.empresa_id IS NULL AND r.nombre = N'Administrador';

-- Técnico
INSERT INTO dbo.rol_permiso (rol_id, permiso_id)
SELECT r.id, p.id
FROM dbo.roles r JOIN dbo.permisos p ON p.clave IN (
    N'dashboard.ver', N'equipos.ver', N'equipos.editar', N'alertas.ver', N'alertas.gestionar',
    N'tickets.crear', N'tickets.gestionar', N'remoto.conectar', N'remoto.terminal',
    N'remoto.archivos', N'scripts.ejecutar', N'red.ver', N'reportes.ver')
WHERE r.empresa_id IS NULL AND r.nombre = N'Tecnico';

-- Red
INSERT INTO dbo.rol_permiso (rol_id, permiso_id)
SELECT r.id, p.id
FROM dbo.roles r JOIN dbo.permisos p ON p.clave IN (
    N'dashboard.ver', N'equipos.ver', N'alertas.ver', N'alertas.gestionar',
    N'tickets.crear', N'tickets.gestionar', N'red.ver', N'red.editar', N'reportes.ver')
WHERE r.empresa_id IS NULL AND r.nombre = N'Red';

-- Supervisor
INSERT INTO dbo.rol_permiso (rol_id, permiso_id)
SELECT r.id, p.id
FROM dbo.roles r JOIN dbo.permisos p ON p.clave IN (
    N'dashboard.ver', N'equipos.ver', N'alertas.ver', N'red.ver', N'reportes.ver', N'auditoria.ver')
WHERE r.empresa_id IS NULL AND r.nombre = N'Supervisor';

-- Empleado
INSERT INTO dbo.rol_permiso (rol_id, permiso_id)
SELECT r.id, p.id
FROM dbo.roles r JOIN dbo.permisos p ON p.clave IN (N'equipos.ver', N'tickets.crear')
WHERE r.empresa_id IS NULL AND r.nombre = N'Empleado';
GO

PRINT N'Esquema MonitoreoTI creado: 45 tablas, índices, vista, procedimiento y datos iniciales.';
GO
