/* =====================================================================
   00. CREACIÓN DE LA BASE DE DATOS
   ---------------------------------------------------------------------
   Separado del esquema para poder crear varias bases con los mismos
   scripts (MonitoreoTI, MonitoreoTI_Tests...).

   Con sqlcmd:   sqlcmd ... -v DbName="MonitoreoTI" -i 00_create_database.sql
   En SSMS:      activar Query > SQLCMD Mode, o reemplazar $(DbName).
   ===================================================================== */
IF DB_ID(N'$(DbName)') IS NULL
    CREATE DATABASE [$(DbName)] COLLATE Modern_Spanish_CI_AS;
GO

ALTER DATABASE [$(DbName)] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
