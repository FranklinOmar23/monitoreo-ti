/* =====================================================================
   03. DATOS DE PRUEBA — SOLO DESARROLLO
   ---------------------------------------------------------------------
   Dos empresas para probar el aislamiento:
     - Grupo Comercial Caribe (plan Empresarial): 5 sucursales, equipos,
       data center con gabinetes, switches, cableado y tabla MAC.
     - Clínica San Rafael (plan Básico): 1 sucursal y nombres repetidos a
       propósito (CONTA-03, SW-CORE-01) para validar los UNIQUE por empresa.

   Usuarios (todos con la contraseña de desarrollo: MonitoreoTI.Dev1)
     soporte@monitoreoti.local   Superadministrador (sin empresa)
     admin@caribe.test           Administrador  - Caribe
     tecnico@caribe.test         Tecnico        - Caribe (Santo Domingo y Santiago)
     red@caribe.test             Red            - Caribe
     admin@sanrafael.test        Administrador  - San Rafael
     multi@monitoreoti.test      Supervisor en Caribe y Tecnico en San Rafael

   Requiere 01_schema.sql y 02_multitenant.sql. Se ejecuta con contexto de
   superadministrador para poder escribir en ambas empresas.
   ===================================================================== */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

EXEC sp_set_session_context N'es_superadmin', 1;
GO

BEGIN TRANSACTION;

DECLARE @hash NVARCHAR(255) = N'$2a$11$5ClYslCOlky5juQNsD7OsOtqRPh0OiC8tkEc4FrXYWbX5NvuGB5ZK';
DECLARE @ahora DATETIME2(0) = SYSUTCDATETIME();

/* ---------------------------------------------------------------------
   Empresas y suscripciones
   --------------------------------------------------------------------- */
INSERT INTO dbo.empresas (nombre, rnc, correo_contacto, telefono, plan_id)
SELECT N'Grupo Comercial Caribe', '130000001', N'ti@caribe.test', '809-555-0100', id
FROM dbo.planes WHERE nombre = N'Empresarial';

INSERT INTO dbo.empresas (nombre, rnc, correo_contacto, telefono, plan_id)
SELECT N'Clínica San Rafael', '130000002', N'sistemas@sanrafael.test', '809-555-0200', id
FROM dbo.planes WHERE nombre = N'Básico';

DECLARE @caribe INT = (SELECT id FROM dbo.empresas WHERE nombre = N'Grupo Comercial Caribe');
DECLARE @rafael INT = (SELECT id FROM dbo.empresas WHERE nombre = N'Clínica San Rafael');

INSERT INTO dbo.suscripciones (empresa_id, plan_id, inicio)
SELECT id, plan_id, '2026-01-01' FROM dbo.empresas;

/* ---------------------------------------------------------------------
   Usuarios
   --------------------------------------------------------------------- */
INSERT INTO dbo.usuarios (nombre, correo, password_hash, es_superadmin) VALUES
    (N'Soporte MonitoreoTI',   N'soporte@monitoreoti.local', @hash, 1),
    (N'Ana Martínez',          N'admin@caribe.test',         @hash, 0),
    (N'Luis Peña',             N'tecnico@caribe.test',       @hash, 0),
    (N'Carlos Rosario',        N'red@caribe.test',           @hash, 0),
    (N'María Jiménez',         N'admin@sanrafael.test',      @hash, 0),
    (N'Pedro Santana',         N'multi@monitoreoti.test',    @hash, 0);

INSERT INTO dbo.usuario_empresa (usuario_id, empresa_id, rol_id)
SELECT u.id, x.empresa_id, r.id
FROM (VALUES
    (N'admin@caribe.test',      @caribe, N'Administrador'),
    (N'tecnico@caribe.test',    @caribe, N'Tecnico'),
    (N'red@caribe.test',        @caribe, N'Red'),
    (N'admin@sanrafael.test',   @rafael, N'Administrador'),
    (N'multi@monitoreoti.test', @caribe, N'Supervisor'),
    (N'multi@monitoreoti.test', @rafael, N'Tecnico')
) AS x (correo, empresa_id, rol)
JOIN dbo.usuarios u ON u.correo = x.correo
JOIN dbo.roles r    ON r.nombre = x.rol AND r.empresa_id IS NULL;

DECLARE @admin_caribe INT = (SELECT id FROM dbo.usuarios WHERE correo = N'admin@caribe.test');
DECLARE @tecnico      INT = (SELECT id FROM dbo.usuarios WHERE correo = N'tecnico@caribe.test');
DECLARE @admin_rafael INT = (SELECT id FROM dbo.usuarios WHERE correo = N'admin@sanrafael.test');

/* ---------------------------------------------------------------------
   Sucursales y departamentos
   --------------------------------------------------------------------- */
INSERT INTO dbo.sucursales (empresa_id, nombre, provincia, direccion, responsable_id) VALUES
    (@caribe, N'Santo Domingo',          N'Distrito Nacional',      N'Av. Winston Churchill 1050', @admin_caribe),
    (@caribe, N'Santiago',               N'Santiago',               N'Av. 27 de Febrero 45',       @tecnico),
    (@caribe, N'La Romana',              N'La Romana',              N'Calle Duarte 12',            NULL),
    (@caribe, N'Puerto Plata',           N'Puerto Plata',           N'Calle Beller 30',            NULL),
    (@caribe, N'San Pedro de Macorís',   N'San Pedro de Macorís',   N'Av. Independencia 8',        NULL),
    (@rafael, N'Santo Domingo',          N'Distrito Nacional',      N'Av. Abraham Lincoln 300',    @admin_rafael);

DECLARE @sd       INT = (SELECT id FROM dbo.sucursales WHERE empresa_id = @caribe AND nombre = N'Santo Domingo');
DECLARE @santiago INT = (SELECT id FROM dbo.sucursales WHERE empresa_id = @caribe AND nombre = N'Santiago');
DECLARE @romana   INT = (SELECT id FROM dbo.sucursales WHERE empresa_id = @caribe AND nombre = N'La Romana');
DECLARE @sd_rafael INT = (SELECT id FROM dbo.sucursales WHERE empresa_id = @rafael AND nombre = N'Santo Domingo');

INSERT INTO dbo.usuario_sucursal (empresa_id, usuario_id, sucursal_id) VALUES
    (@caribe, @tecnico, @sd),
    (@caribe, @tecnico, @santiago);

INSERT INTO dbo.departamentos (empresa_id, nombre) VALUES
    (@caribe, N'Contabilidad'), (@caribe, N'Caja'), (@caribe, N'Recursos Humanos'),
    (@caribe, N'Ventas'), (@caribe, N'TI'), (@caribe, N'Recepción'),
    (@rafael, N'Contabilidad');

/* ---------------------------------------------------------------------
   Máquinas
   --------------------------------------------------------------------- */
INSERT INTO dbo.maquinas (empresa_id, sucursal_id, departamento_id, nombre, tipo, marca, modelo, serie,
                          sistema_operativo, ip, mac, responsable, fecha_compra, garantia_hasta, estado, ultimo_reporte)
SELECT x.empresa_id, x.sucursal_id, d.id, x.nombre, x.tipo, x.marca, x.modelo, x.serie,
       x.so, x.ip, x.mac, x.responsable, x.compra, x.garantia, x.estado,
       CASE WHEN x.estado = 'desconectado' THEN DATEADD(HOUR, -5, @ahora) ELSE @ahora END
FROM (VALUES
    (@caribe, @sd,        N'Contabilidad',     N'CONTA-03',      'computadora', N'Dell',  N'OptiPlex 7010',   N'DL7010-0003', N'Windows 11 Pro 23H2',            '10.10.20.31', '3C:52:82:A1:00:03', N'Rosa Guzmán',   CAST('2024-03-15' AS DATE), CAST('2027-03-15' AS DATE), 'sano'),
    (@caribe, @santiago,  N'Caja',             N'CAJA-01',       'computadora', N'HP',    N'ProDesk 400 G9',  N'HP400-0001',  N'Windows 10 Pro 22H2',            '10.20.20.11', '3C:52:82:B2:00:01', N'José Almonte',  '2022-06-01', '2025-06-01', 'advertencia'),
    (@caribe, @sd,        N'Recursos Humanos', N'RRHH-02',       'laptop',      N'Lenovo',N'ThinkPad E14',    N'LNE14-0002',  N'Windows 11 Pro 23H2',            '10.10.20.42', '3C:52:82:A1:00:42', N'Carmen Reyes',  '2023-01-20', '2026-01-20', 'critico'),
    (@caribe, @romana,    N'Ventas',           N'VENTAS-05',     'computadora', N'Dell',  N'Vostro 3020',     N'DV3020-0005', N'Windows 11 Pro 23H2',            '10.30.20.15', '3C:52:82:C3:00:05', N'Miguel Batista','2024-08-10', '2027-08-10', 'desconectado'),
    (@caribe, @sd,        N'TI',               N'SERVIDOR-01',   'servidor',    N'Dell',  N'PowerEdge R650',  N'PER650-0001', N'Windows Server 2022 Standard',   '10.10.10.5',  '3C:52:82:A1:10:05', N'TI',            '2023-05-05', '2028-05-05', 'sano'),
    (@caribe, @sd,        N'Recepción',        N'IMP-RECEPCION', 'impresora',   N'HP',    N'LaserJet M507',   N'HPM507-0001', NULL,                              '10.10.30.20', '3C:52:82:A1:30:20', N'Recepción',     '2022-11-11', '2024-11-11', 'sano'),
    (@rafael, @sd_rafael, N'Contabilidad',     N'CONTA-03',      'computadora', N'HP',    N'EliteDesk 800 G6',N'HP800-R003',  N'Windows 11 Pro 23H2',            '192.168.1.33','5C:60:BA:00:00:33', N'Laura Féliz',   '2023-09-09', '2026-09-09', 'sano')
) AS x (empresa_id, sucursal_id, departamento, nombre, tipo, marca, modelo, serie, so, ip, mac, responsable, compra, garantia, estado)
LEFT JOIN dbo.departamentos d ON d.empresa_id = x.empresa_id AND d.nombre = x.departamento;

DECLARE @conta    INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'CONTA-03');
DECLARE @caja     INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'CAJA-01');
DECLARE @rrhh     INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'RRHH-02');
DECLARE @ventas   INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'VENTAS-05');
DECLARE @servidor INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'SERVIDOR-01');
DECLARE @impresora INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @caribe AND nombre = N'IMP-RECEPCION');
DECLARE @conta_rafael INT = (SELECT id FROM dbo.maquinas WHERE empresa_id = @rafael AND nombre = N'CONTA-03');

-- Componentes
INSERT INTO dbo.componentes (empresa_id, maquina_id, tipo, descripcion, capacidad, estado_salud)
SELECT m.empresa_id, m.id, c.tipo, c.descripcion, c.capacidad,
       CASE WHEN m.id = @rrhh AND c.tipo = 'disco' THEN 'critico'
            WHEN m.id = @caja AND c.tipo = 'ram'   THEN 'advertencia'
            ELSE 'sano' END
FROM dbo.maquinas m
CROSS JOIN (VALUES
    ('cpu',   N'Intel Core i5-13500', N'14 núcleos'),
    ('ram',   N'DDR4 3200 MHz',       N'16 GB'),
    ('disco', N'NVMe SSD',            N'512 GB SSD'),
    ('red',   N'Intel Ethernet I219', N'1 Gbps')
) AS c (tipo, descripcion, capacidad)
WHERE m.tipo IN ('computadora','laptop','servidor');

-- Software
INSERT INTO dbo.software_instalado (empresa_id, maquina_id, nombre, version, editor, fecha_instalacion)
SELECT m.empresa_id, m.id, s.nombre, s.version, s.editor, '2025-01-10'
FROM dbo.maquinas m
CROSS JOIN (VALUES
    (N'Google Chrome',          N'130.0.6723.70', N'Google LLC'),
    (N'Microsoft 365 Apps',     N'16.0.18025',    N'Microsoft Corporation'),
    (N'7-Zip 24.08 (x64)',      N'24.08',         N'Igor Pavlov')
) AS s (nombre, version, editor)
WHERE m.tipo IN ('computadora','laptop','servidor');

-- Interfaces de red (la MAC principal coincide con maquinas.mac)
INSERT INTO dbo.interfaces_red (empresa_id, maquina_id, nombre, mac, ip, tipo, velocidad_mbps)
SELECT empresa_id, id, N'Ethernet', mac, ip, 'cableada', 1000
FROM dbo.maquinas WHERE mac IS NOT NULL;

INSERT INTO dbo.interfaces_red (empresa_id, maquina_id, nombre, mac, ip, tipo, velocidad_mbps) VALUES
    (@caribe, @rrhh, N'Wi-Fi', '3C:52:82:A1:F0:42', NULL, 'wifi', 866);

-- Agentes (token_hash no válido: los agentes reales se registran con token de sucursal)
INSERT INTO dbo.agentes (empresa_id, maquina_id, version, token_hash, es_recolector, ultimo_contacto)
SELECT m.empresa_id, m.id, '0.1.0', N'dev-no-valido-' + CAST(m.id AS NVARCHAR(10)),
       CASE WHEN m.id = @servidor THEN 1 ELSE 0 END, m.ultimo_reporte
FROM dbo.maquinas m
WHERE m.tipo IN ('computadora','laptop','servidor');

-- Tokens de registro de desarrollo (formato "{empresaId}.{secreto}"; se guarda el SHA-256 en hex).
--   Caribe, Santo Domingo:  <id empresa>.DEV-ENROLL-SANTO-DOMINGO  (vence 2030, sin límite de usos)
--   San Rafael:             <id empresa>.DEV-ENROLL-SAN-RAFAEL
INSERT INTO dbo.tokens_registro (empresa_id, sucursal_id, token_hash, vence, usos_max, creado_por)
SELECT x.empresa_id, x.sucursal_id,
       LOWER(CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', CAST(x.empresa_id AS VARCHAR(10)) + '.' + x.secreto), 2)),
       '2030-01-01', NULL, x.creado_por
FROM (VALUES
    (@caribe, @sd,        'DEV-ENROLL-SANTO-DOMINGO', @admin_caribe),
    (@rafael, @sd_rafael, 'DEV-ENROLL-SAN-RAFAEL',    @admin_rafael)
) AS x (empresa_id, sucursal_id, secreto, creado_por);

DECLARE @agente_recolector INT = (SELECT id FROM dbo.agentes WHERE maquina_id = @servidor);

/* ---------------------------------------------------------------------
   Monitoreo: métricas de las últimas 2 horas, eventos y procesos
   --------------------------------------------------------------------- */
;WITH n AS (
    SELECT TOP (120) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects
)
INSERT INTO dbo.metricas (empresa_id, maquina_id, fecha_hora, cpu, ram, disco, temperatura)
SELECT m.empresa_id, m.id, DATEADD(MINUTE, -n.i, @ahora),
       CASE WHEN m.id = @caja THEN 70 ELSE 15 END + ABS(CHECKSUM(NEWID())) % 25,
       CASE WHEN m.id = @caja THEN 86 ELSE 40 END + ABS(CHECKSUM(NEWID())) % 10,
       CASE WHEN m.id = @rrhh THEN 96.5 ELSE 55 END,
       45 + ABS(CHECKSUM(NEWID())) % 15
FROM dbo.maquinas m
CROSS JOIN n
WHERE m.tipo IN ('computadora','laptop','servidor') AND m.estado <> 'desconectado';

INSERT INTO dbo.eventos_error (empresa_id, maquina_id, fecha_hora, origen, codigo, nivel, mensaje, mensaje_simple) VALUES
    (@caribe, @rrhh, DATEADD(HOUR, -3, @ahora), N'Microsoft-Windows-Kernel-Power', 41, 'critico',
     N'The system has rebooted without cleanly shutting down first.', N'El equipo se apagó de forma inesperada'),
    (@caribe, @rrhh, DATEADD(HOUR, -2, @ahora), N'disk', 7, 'error',
     N'The device, \Device\Harddisk0\DR0, has a bad block.', N'Errores de lectura en el disco'),
    (@caribe, @caja, DATEADD(HOUR, -1, @ahora), N'Application Error', 1000, 'error',
     N'Faulting application name: pos.exe', N'Una aplicación se cerró por un error');

INSERT INTO dbo.procesos_snapshot (empresa_id, maquina_id, nombre, pid, cpu, ram_mb)
SELECT m.empresa_id, m.id, p.nombre, p.pid, p.cpu, p.ram_mb
FROM dbo.maquinas m
CROSS JOIN (VALUES
    (N'chrome.exe',  4120, 12.5, 850.0),
    (N'OUTLOOK.EXE', 5232,  3.1, 320.4),
    (N'explorer.exe', 980,  0.8, 110.2)
) AS p (nombre, pid, cpu, ram_mb)
WHERE m.tipo IN ('computadora','laptop') AND m.estado <> 'desconectado';

/* ---------------------------------------------------------------------
   Reglas de alerta por defecto (sección 14), para cada empresa
   --------------------------------------------------------------------- */
INSERT INTO dbo.reglas_alerta (empresa_id, nombre, metrica, operador, umbral_advertencia, umbral_critico, duracion_min)
SELECT e.id, r.nombre, r.metrica, r.operador, r.adv, r.crit, r.duracion
FROM dbo.empresas e
CROSS JOIN (VALUES
    (N'Disco usado',                     'disco',                '>',  80,   95,   0),
    (N'CPU sostenida',                   'cpu',                  '>',  85,   95,   15),
    (N'RAM',                             'ram',                  '>',  85,   95,   0),
    (N'Temperatura CPU',                 'temperatura',          '>',  80,   90,   0),
    (N'Sin reportar (minutos)',          'sin_reportar',         '>=', 10,   30,   0),
    (N'Errores críticos en 24 h',        'errores_criticos',     '>=', 1,    3,    0),
    (N'Servicio automático detenido',    'servicio_detenido',    '>=', NULL, 1,    0),
    (N'Tóner (%)',                       'toner',                '<',  20,   5,    0),
    (N'Antivirus desactivado',           'antivirus',            '=',  NULL, 0,    0),
    (N'Errores en puerto por hora',      'puerto_errores',       '>',  100,  1000, 0),
    (N'Puerto con equipo conocido caído','puerto_caido',         '>=', NULL, 1,    0),
    (N'Switch sin responder (minutos)',  'switch_sin_respuesta', '>=', 10,   30,   0)
) AS r (nombre, metrica, operador, adv, crit, duracion);

/* ---------------------------------------------------------------------
   Red y data center (Caribe, Santo Domingo)
   --------------------------------------------------------------------- */
INSERT INTO dbo.salas (empresa_id, sucursal_id, nombre, ubicacion) VALUES
    (@caribe, @sd, N'Data Center Principal', N'Segundo piso, cuarto 204');

DECLARE @sala INT = (SELECT id FROM dbo.salas WHERE empresa_id = @caribe AND nombre = N'Data Center Principal');

INSERT INTO dbo.gabinetes (empresa_id, sala_id, nombre, altura_u, fila, posicion) VALUES
    (@caribe, @sala, N'GAB-A01', 42, N'A', 1),
    (@caribe, @sala, N'GAB-A02', 42, N'A', 2);

DECLARE @gab1 INT = (SELECT id FROM dbo.gabinetes WHERE empresa_id = @caribe AND nombre = N'GAB-A01');
DECLARE @gab2 INT = (SELECT id FROM dbo.gabinetes WHERE empresa_id = @caribe AND nombre = N'GAB-A02');

-- Catálogo base global
INSERT INTO dbo.modelos_dispositivo (empresa_id, marca, modelo, tipo, altura_u, cantidad_puertos, tipo_puertos, es_activo) VALUES
    (NULL, N'Cisco',   N'Catalyst 9300-48P', 'switch',      1, 52, N'48 RJ45 + 4 SFP+', 1),
    (NULL, N'Cisco',   N'Catalyst 9200-48P', 'switch',      1, 52, N'48 RJ45 + 4 SFP+', 1),
    (NULL, N'Panduit', N'CP48BLY',           'patch_panel', 2, 48, N'48 RJ45 Cat6',     0),
    (NULL, N'APC',     N'Smart-UPS 3000',    'ups',         2, 0,  NULL,                1);

DECLARE @mod_core INT = (SELECT id FROM dbo.modelos_dispositivo WHERE modelo = N'Catalyst 9300-48P');
DECLARE @mod_acc  INT = (SELECT id FROM dbo.modelos_dispositivo WHERE modelo = N'Catalyst 9200-48P');
DECLARE @mod_pp   INT = (SELECT id FROM dbo.modelos_dispositivo WHERE modelo = N'CP48BLY');
DECLARE @mod_ups  INT = (SELECT id FROM dbo.modelos_dispositivo WHERE modelo = N'Smart-UPS 3000');

INSERT INTO dbo.dispositivos_red (empresa_id, sucursal_id, gabinete_id, modelo_id, recolector_id, nombre, tipo, ip_gestion,
                                  posicion_u, altura_u, firmware, estado, ultimo_sondeo) VALUES
    (@caribe, @sd,        @gab1, @mod_core, @agente_recolector, N'SW-CORE-01', 'switch',      '10.10.0.1', 40, 1, N'IOS-XE 17.9.4', 'en_linea', @ahora),
    (@caribe, @sd,        @gab1, @mod_acc,  @agente_recolector, N'SW-ACC-02',  'switch',      '10.10.0.2', 38, 1, N'IOS-XE 17.9.4', 'advertencia', @ahora),
    (@caribe, @sd,        @gab1, @mod_pp,   NULL,               N'PP-A01-01',  'patch_panel', NULL,        35, 2, NULL,             'pasivo', NULL),
    (@caribe, @sd,        @gab2, @mod_acc,  @agente_recolector, N'SW-ACC-03',  'switch',      '10.10.0.3', 40, 1, N'IOS-XE 17.9.4', 'en_linea', @ahora),
    (@caribe, @sd,        @gab2, @mod_ups,  @agente_recolector, N'UPS-A02-01', 'ups',         '10.10.0.9', 1,  2, NULL,             'en_linea', @ahora),
    (@rafael, @sd_rafael, NULL,  @mod_acc,  NULL,               N'SW-CORE-01', 'switch',      '192.168.1.1', NULL, 1, NULL,         'sin_respuesta', NULL);

-- Puertos según el modelo: RJ45 Gi1/0/n y luego SFP+ Te1/1/n
;WITH n AS (
    SELECT TOP (64) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects
)
INSERT INTO dbo.puertos (empresa_id, dispositivo_id, numero, nombre, tipo, vlan, velocidad_mbps, duplex, estado)
SELECT d.empresa_id, d.id, n.i,
       CASE WHEN md.tipo = 'patch_panel' THEN CONCAT(N'P', n.i)
            WHEN n.i <= 48 THEN CONCAT(N'Gi1/0/', n.i)
            ELSE CONCAT(N'Te1/1/', n.i - 48) END,
       CASE WHEN n.i <= 48 THEN 'rj45' ELSE 'sfp_plus' END,
       CASE WHEN md.tipo = 'switch' AND n.i <= 48 THEN 20 END,
       NULL, NULL, 'libre'
FROM dbo.dispositivos_red d
JOIN dbo.modelos_dispositivo md ON md.id = d.modelo_id
JOIN n ON n.i <= md.cantidad_puertos;

DECLARE @core  INT = (SELECT id FROM dbo.dispositivos_red WHERE empresa_id = @caribe AND nombre = N'SW-CORE-01');
DECLARE @acc2  INT = (SELECT id FROM dbo.dispositivos_red WHERE empresa_id = @caribe AND nombre = N'SW-ACC-02');
DECLARE @acc3  INT = (SELECT id FROM dbo.dispositivos_red WHERE empresa_id = @caribe AND nombre = N'SW-ACC-03');
DECLARE @pp    INT = (SELECT id FROM dbo.dispositivos_red WHERE empresa_id = @caribe AND nombre = N'PP-A01-01');

-- Uplinks troncales: ACC-02 Te1/1/1 -> CORE Te1/1/1, ACC-03 Te1/1/1 -> CORE Te1/1/2
UPDATE dbo.puertos SET es_troncal = 1, estado = 'conectado', velocidad_mbps = 10000, duplex = 'full', vlan = NULL
WHERE (dispositivo_id = @acc2 AND numero = 49)
   OR (dispositivo_id = @acc3 AND numero = 49)
   OR (dispositivo_id = @core AND numero IN (49, 50));

INSERT INTO dbo.enlaces_red (empresa_id, puerto_origen_id, puerto_destino_id, protocolo)
SELECT @caribe, o.id, d.id, 'lldp'
FROM (VALUES (@acc2, 49, 49), (@acc3, 49, 50)) AS x (origen_disp, origen_num, core_num)
JOIN dbo.puertos o ON o.dispositivo_id = x.origen_disp AND o.numero = x.origen_num
JOIN dbo.puertos d ON d.dispositivo_id = @core       AND d.numero = x.core_num;

-- Puertos de acceso con equipos
UPDATE p SET estado = x.estado, velocidad_mbps = 1000, duplex = 'full'
FROM dbo.puertos p
JOIN (VALUES
    (@acc2, 12, 'conectado'),   -- CONTA-03
    (@acc2, 15, 'errores'),     -- RRHH-02
    (@core,  5, 'conectado'),   -- SERVIDOR-01
    (@acc3,  3, 'conectado'),   -- IMP-RECEPCION
    (@acc3,  7, 'conectado'),   -- equipo desconocido
    (@acc3, 20, 'caido')
) AS x (dispositivo_id, numero, estado) ON x.dispositivo_id = p.dispositivo_id AND x.numero = p.numero;

INSERT INTO dbo.tabla_mac (empresa_id, puerto_id, maquina_id, mac, ip, vlan, fabricante)
SELECT @caribe, p.id, x.maquina_id, x.mac, x.ip, 20, x.fabricante
FROM (VALUES
    (@acc2, 12, @conta,     '3C:52:82:A1:00:03', '10.10.20.31', N'Dell Inc.'),
    (@acc2, 15, @rrhh,      '3C:52:82:A1:00:42', '10.10.20.42', N'Lenovo'),
    (@core,  5, @servidor,  '3C:52:82:A1:10:05', '10.10.10.5',  N'Dell Inc.'),
    (@acc3,  3, @impresora, '3C:52:82:A1:30:20', '10.10.30.20', N'HP Inc.'),
    (@acc3,  7, NULL,       'F4:F5:D8:12:34:56', '10.10.20.77', N'Google, Inc.')
) AS x (dispositivo_id, numero, maquina_id, mac, ip, fabricante)
JOIN dbo.puertos p ON p.dispositivo_id = x.dispositivo_id AND p.numero = x.numero;

-- Métricas de puerto recientes
INSERT INTO dbo.metricas_puerto (empresa_id, puerto_id, fecha_hora, bytes_entrada, bytes_salida, errores_entrada, errores_salida, descartes)
SELECT p.empresa_id, p.id, @ahora, 1250000000, 830000000,
       CASE WHEN p.estado = 'errores' THEN 450 ELSE 0 END, 0, 0
FROM dbo.puertos p
WHERE p.empresa_id = @caribe AND p.estado IN ('conectado','errores');

-- Roseta y cableado documentado: CONTA-03 -> P-2-15 -> PP-A01-01 P12 -> SW-ACC-02 Gi1/0/12
INSERT INTO dbo.puntos_red (empresa_id, sucursal_id, codigo, ubicacion) VALUES
    (@caribe, @sd, N'P-2-15', N'Contabilidad, escritorio 3'),
    (@caribe, @sd, N'P-2-18', N'Recursos Humanos, escritorio 1');

INSERT INTO dbo.cableado (empresa_id, puerto_a_id, puerto_b_id, punto_red_id, tipo_cable, color, etiqueta)
SELECT @caribe, pp.id, NULL, pr.id, 'cat6', N'Azul', N'P-2-15'
FROM dbo.puertos pp
JOIN dbo.puntos_red pr ON pr.empresa_id = @caribe AND pr.codigo = N'P-2-15'
WHERE pp.dispositivo_id = @pp AND pp.numero = 12;

INSERT INTO dbo.cableado (empresa_id, puerto_a_id, puerto_b_id, punto_red_id, tipo_cable, color, etiqueta)
SELECT @caribe, pp.id, sw.id, NULL, 'cat6', N'Amarillo', N'PP12-ACC02-12'
FROM dbo.puertos pp
JOIN dbo.puertos sw ON sw.dispositivo_id = @acc2 AND sw.numero = 12
WHERE pp.dispositivo_id = @pp AND pp.numero = 12;

/* ---------------------------------------------------------------------
   Alertas, tickets y mantenimientos
   --------------------------------------------------------------------- */
INSERT INTO dbo.alertas (empresa_id, maquina_id, regla_id, nivel, estado, mensaje, creada)
SELECT @caribe, @rrhh, r.id, 'critico', 'nueva', N'Disco C: al 96,5 % en RRHH-02', DATEADD(MINUTE, -40, @ahora)
FROM dbo.reglas_alerta r WHERE r.empresa_id = @caribe AND r.metrica = 'disco';

INSERT INTO dbo.alertas (empresa_id, maquina_id, regla_id, nivel, estado, mensaje, creada)
SELECT @caribe, @caja, r.id, 'advertencia', 'en_proceso', N'RAM al 90 % en CAJA-01', DATEADD(MINUTE, -25, @ahora)
FROM dbo.reglas_alerta r WHERE r.empresa_id = @caribe AND r.metrica = 'ram';

INSERT INTO dbo.alertas (empresa_id, puerto_id, regla_id, nivel, estado, mensaje, creada)
SELECT @caribe, p.id, r.id, 'advertencia', 'nueva', N'450 errores de entrada en SW-ACC-02 Gi1/0/15', DATEADD(MINUTE, -10, @ahora)
FROM dbo.puertos p
JOIN dbo.reglas_alerta r ON r.empresa_id = @caribe AND r.metrica = 'puerto_errores'
WHERE p.dispositivo_id = @acc2 AND p.numero = 15;

INSERT INTO dbo.alertas (empresa_id, maquina_id, nivel, estado, mensaje, creada)
VALUES (@caribe, @ventas, 'critico', 'nueva', N'VENTAS-05 lleva 5 horas sin reportar', DATEADD(HOUR, -4, @ahora));

INSERT INTO dbo.tickets (empresa_id, maquina_id, alerta_id, creado_por, asignado_a, titulo, descripcion, prioridad, estado)
SELECT @caribe, @rrhh, a.id, @admin_caribe, @tecnico,
       N'Disco casi lleno y con errores en RRHH-02',
       N'Revisar el disco: tiene sectores defectuosos y está al 96 %. Evaluar reemplazo.',
       'alta', 'en_proceso'
FROM dbo.alertas a WHERE a.empresa_id = @caribe AND a.maquina_id = @rrhh;

INSERT INTO dbo.ticket_comentarios (empresa_id, ticket_id, usuario_id, texto)
SELECT @caribe, t.id, @tecnico, N'Respaldé los documentos del usuario. Pido el disco de reemplazo.'
FROM dbo.tickets t WHERE t.empresa_id = @caribe;

INSERT INTO dbo.mantenimientos (empresa_id, maquina_id, tecnico_id, tipo, descripcion, fecha_programada) VALUES
    (@caribe, @servidor, @tecnico, 'preventivo', N'Limpieza física y revisión de discos RAID', '2026-11-15'),
    (@caribe, @rrhh,     @tecnico, 'correctivo', N'Reemplazo de disco SSD',                   '2026-10-30');

/* ---------------------------------------------------------------------
   Datos mínimos de San Rafael
   --------------------------------------------------------------------- */
INSERT INTO dbo.alertas (empresa_id, maquina_id, nivel, estado, mensaje)
VALUES (@rafael, @conta_rafael, 'advertencia', 'nueva', N'CPU al 88 % en CONTA-03');

/* ---------------------------------------------------------------------
   Historial del semáforo (gráfica de 7 días): días anteriores a partir
   del estado actual con pequeñas variaciones. Hoy lo escribe el servidor.
   --------------------------------------------------------------------- */
;WITH dias AS (
    SELECT TOP (6) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS d FROM sys.all_objects
),
actual AS (
    SELECT empresa_id,
           SUM(CASE WHEN estado = 'sano' THEN 1 ELSE 0 END)         AS sano,
           SUM(CASE WHEN estado = 'advertencia' THEN 1 ELSE 0 END)  AS advertencia,
           SUM(CASE WHEN estado = 'critico' THEN 1 ELSE 0 END)      AS critico,
           SUM(CASE WHEN estado = 'desconectado' THEN 1 ELSE 0 END) AS desconectado
    FROM dbo.maquinas WHERE estado <> 'baja'
    GROUP BY empresa_id
)
INSERT INTO dbo.estado_diario (empresa_id, fecha, sano, advertencia, critico, desconectado)
SELECT a.empresa_id, CAST(DATEADD(DAY, -d.d, @ahora) AS DATE),
       a.sano + (d.d % 2), a.advertencia, CASE WHEN d.d % 3 = 0 THEN 0 ELSE a.critico END,
       CASE WHEN a.desconectado > 0 AND d.d % 2 = 1 THEN a.desconectado - 1 ELSE a.desconectado END
FROM actual a CROSS JOIN dias d;

COMMIT TRANSACTION;
GO

PRINT N'Datos de prueba cargados: 2 empresas, 6 sucursales, 7 equipos, 6 dispositivos de red.';
GO
