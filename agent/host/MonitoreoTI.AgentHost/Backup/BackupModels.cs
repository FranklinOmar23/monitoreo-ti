using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>JSON de los respaldos (decisión 82): todo en snake_case, como lo manda el agente.</summary>
public static class BackupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class BackupDiskRef
{
    public string Serie { get; set; } = "";
    public string Nombre { get; set; } = "";
}

/// <summary>Dónde se guarda. La cuenta de la carpeta de red llega ya abierta por el agente (solo en memoria).</summary>
public sealed class BackupDestination
{
    /// <summary>disco_externo, red o local.</summary>
    public string Tipo { get; set; } = "";
    public string Ruta { get; set; } = "";
    public List<BackupDiskRef> Discos { get; set; } = [];
    public bool Expulsar { get; set; }
    public string? Usuario { get; set; }
    public string? Clave { get; set; }
}

public sealed class BackupJob
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";

    /// <summary>sql o carpetas.</summary>
    public string Origen { get; set; } = "";
    public string? InstanciaSql { get; set; }
    public List<string> Elementos { get; set; } = [];

    /// <summary>caliente o frio.</summary>
    public string Modo { get; set; } = "caliente";
    public List<string> ServiciosDetener { get; set; } = [];
    public int FrioMaxMinutos { get; set; } = 60;

    /// <summary>completo o incremental.</summary>
    public string Estrategia { get; set; } = "incremental";
    public int CompletoCadaDias { get; set; } = 7;
    public BackupDestination Destino { get; set; } = new();
    public int RetenerDiarios { get; set; } = 7;
    public int RetenerMensuales { get; set; } = 3;
    public bool Verificar { get; set; } = true;
}

/// <summary>Lo que recibe "respaldo" por stdin.</summary>
public sealed class BackupRequest
{
    public BackupJob Trabajo { get; set; } = new();
    public string Ejecucion { get; set; } = "";
    public bool ForzarCompleto { get; set; }

    /// <summary>Nombre del servidor: la carpeta del respaldo en el destino.</summary>
    public string Equipo { get; set; } = Environment.MachineName;
}

/// <summary>Una línea de stdout: avance o (con fin) el resultado.</summary>
public sealed class BackupStep
{
    /// <summary>en_curso, correcto, advertencia o error.</summary>
    public string Estado { get; set; } = "en_curso";
    public string? Tipo { get; set; }
    public string? Detalle { get; set; }
    public long BytesCopiados { get; set; }
    public long BytesTotales { get; set; }
    public int? Archivos { get; set; }
    public string? Destino { get; set; }
    public string? Conjunto { get; set; }
    public List<string>? ServiciosSinArrancar { get; set; }
    public bool? Fin { get; set; }
}

/// <summary>
/// manifest.json de cada respaldo en el destino. Se escribe al final: un respaldo sin manifiesto quedó a medias y no
/// cuenta (ni como base de los siguientes).
/// </summary>
public sealed class BackupManifest
{
    public int Version { get; set; } = 1;
    public int TrabajoId { get; set; }
    public string Conjunto { get; set; } = "";
    public DateTime Fecha { get; set; }

    /// <summary>completo, incremental o diferencial.</summary>
    public string Tipo { get; set; } = "completo";

    /// <summary>La copia completa en la que se apoya (null si es completa).</summary>
    public string? Base { get; set; }

    /// <summary>Todos los respaldos que hacen falta para restaurar este (incluido él).</summary>
    public List<string> Depende { get; set; } = [];
    public string Origen { get; set; } = "";
    public List<string> Elementos { get; set; } = [];
    public long BytesCopiados { get; set; }
    public long BytesTotales { get; set; }

    /// <summary>Carpetas: el estado completo de los archivos en este momento y en qué respaldo está cada uno.</summary>
    public List<ManifestFile>? Archivos { get; set; }

    /// <summary>SQL Server: un archivo .bak por base.</summary>
    public List<ManifestDatabase>? Bases { get; set; }
}

public sealed class ManifestFile
{
    public string Ruta { get; set; } = "";
    public long Tam { get; set; }

    /// <summary>Última modificación (ticks UTC).</summary>
    public long Mod { get; set; }

    /// <summary>Respaldo que tiene la copia de este archivo.</summary>
    public string En { get; set; } = "";
}

public sealed class ManifestDatabase
{
    public string Nombre { get; set; } = "";
    public string Archivo { get; set; } = "";

    /// <summary>completo o diferencial.</summary>
    public string Tipo { get; set; } = "completo";

    /// <summary>differential_base_guid de la base justo después de la copia completa: si cambia, alguien más hizo otra.</summary>
    public string? BaseGuid { get; set; }
    public long Bytes { get; set; }
}
