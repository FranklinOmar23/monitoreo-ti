<#
.SYNOPSIS
    Borra y recrea una base MonitoreoTI ejecutando los scripts en orden.

.DESCRIPTION
    00_create_database.sql -> 01_schema.sql -> 02_multitenant.sql -> 03_seed_dev.sql
    Opcionalmente crea el login de la aplicación (sin permisos para alterar
    el esquema ni la política de seguridad).

    Las contraseñas se toman de los parámetros o de las variables de entorno
    MONITOREOTI_SQL_PASSWORD (administrador) y MONITOREOTI_APP_PASSWORD (aplicación).
    Nunca se guardan en el repositorio.

.EXAMPLE
    $env:MONITOREOTI_SQL_PASSWORD = '...'
    ./reset-db.ps1 -Server 192.168.4.117,1433 -User sa

.EXAMPLE
    ./reset-db.ps1 -Server 192.168.4.117,1433 -User sa -Database MonitoreoTI_Tests -SkipSeed
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [Parameter(Mandatory)] [string] $User,
    [string] $Password = $env:MONITOREOTI_SQL_PASSWORD,
    [string] $Database = 'MonitoreoTI',
    [switch] $SkipSeed,
    [string] $AppLogin = 'monitoreoti_app',
    [string] $AppPassword = $env:MONITOREOTI_APP_PASSWORD
)

$ErrorActionPreference = 'Stop'

if (-not $Password) { throw 'Falta la contraseña: use -Password o la variable MONITOREOTI_SQL_PASSWORD.' }
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Nombre de base no válido: $Database" }
if ($AppLogin -notmatch '^[A-Za-z0-9_]+$') { throw "Nombre de login no válido: $AppLogin" }

$scripts = $PSScriptRoot
# -b aborta al primer error, -C confía en el certificado del servidor,
# -I activa QUOTED_IDENTIFIER, -f 65001 lee los scripts como UTF-8.
$common = @('-S', $Server, '-U', $User, '-C', '-b', '-I', '-f', '65001')

function Invoke-Sql([string[]] $arguments) {
    $env:SQLCMDPASSWORD = $Password
    try {
        & sqlcmd @common @arguments
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd terminó con código $LASTEXITCODE" }
    }
    finally {
        Remove-Item Env:SQLCMDPASSWORD -ErrorAction SilentlyContinue
    }
}

Write-Host "Borrando la base $Database en $Server..."
Invoke-Sql @('-d', 'master', '-Q', @"
IF DB_ID(N'$Database') IS NOT NULL
BEGIN
    ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$Database];
END
"@)

Write-Host 'Creando la base...'
Invoke-Sql @('-d', 'master', '-v', "DbName=$Database", '-i', (Join-Path $scripts '00_create_database.sql'))

$pasos = @('01_schema.sql', '02_multitenant.sql')
if (-not $SkipSeed) { $pasos += '03_seed_dev.sql' }

foreach ($script in $pasos) {
    Write-Host "Ejecutando $script..."
    Invoke-Sql @('-d', $Database, '-i', (Join-Path $scripts $script))
}

if ($AppPassword) {
    Write-Host "Configurando el login de aplicación $AppLogin..."
    $escaped = $AppPassword.Replace("'", "''")
    Invoke-Sql @('-d', 'master', '-Q', @"
IF SUSER_ID(N'$AppLogin') IS NULL
    CREATE LOGIN [$AppLogin] WITH PASSWORD = N'$escaped', CHECK_POLICY = ON;
ELSE
    ALTER LOGIN [$AppLogin] WITH PASSWORD = N'$escaped';
"@)
    Invoke-Sql @('-d', $Database, '-Q', @"
CREATE USER [$AppLogin] FOR LOGIN [$AppLogin];
ALTER ROLE db_datareader ADD MEMBER [$AppLogin];
ALTER ROLE db_datawriter ADD MEMBER [$AppLogin];
GRANT EXECUTE ON SCHEMA::dbo TO [$AppLogin];
"@)
}

Write-Host "Listo: $Database recreada."
