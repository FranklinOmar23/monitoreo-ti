using System.Security.Cryptography;
using System.Text;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Network;

// ------------------------------------------------------------------ servidor → recolector (bienvenida y config_actualizada)

/// <summary>
/// Algo que el recolector consulta: un dispositivo de red o una impresora (<see cref="Origen"/>). La credencial va
/// sellada para ese agente (ver <see cref="SnmpCredentialSeal"/>): claves v3 {auth, priv} o comunidad v2c {community}.
/// </summary>
public sealed record CollectorDevice(
    int Id, string Nombre, string Ip, string Tipo, string Usuario, string Credencial, string Version = "v3", string Origen = "dispositivo");

public sealed record CollectorConfig(int IntervaloSegundos, IReadOnlyList<CollectorDevice> Dispositivos);

// ------------------------------------------------------------------ recolector → servidor (snmp_resultado)

/// <summary>
/// Resultado de consultar un dispositivo (lo arma el anfitrión C# del agente). Si <see cref="Ok"/> es false solo
/// trae el motivo; las claves nunca viajan de vuelta.
/// </summary>
public sealed record SnmpResultadoData(
    int? DispositivoId, bool Ok, string? Error, SnmpSistemaData? Sistema, IReadOnlyList<SnmpInterfazData>? Interfaces,
    IReadOnlyList<SnmpMacData>? Mac, IReadOnlyList<SnmpArpData>? Arp, IReadOnlyList<SnmpVecinoData>? Lldp,
    /// <summary>Impresoras: el equipo consultado (en vez de un dispositivo de red) y sus consumibles.</summary>
    int? MaquinaId = null, IReadOnlyList<SnmpConsumibleData>? Consumibles = null);

/// <summary>Consumible de impresora (Printer-MIB prtMarkerSupplies). Nivel -3 = "le queda", -2 = desconocido.</summary>
/// <param name="Clase">3 = se consume (tóner, tambor); 4 = depósito que se llena (residuos).</param>
public sealed record SnmpConsumibleData(string Indice, string? Descripcion, int? Nivel, int? Maximo, int? Clase);

public static class PrinterSupplies
{
    /// <summary>Porcentaje restante; null si la impresora no informa un nivel medible.</summary>
    public static int? Percent(int? level, int? max) =>
        level is int l and >= 0 && max is int m and > 0 ? (int)Math.Clamp(Math.Round(100m * l / m), 0, 100) : null;

    public static bool IsWasteReceptacle(int? supplyClass) => supplyClass == 4;
}

public sealed record SnmpSistemaData(string? Descripcion, string? Nombre, long? UptimeSegundos);

public sealed record SnmpInterfazData(
    int Indice, int? Tipo, string? Nombre, string? Alias, string? Admin, string? Operativo, long? VelocidadMbps,
    long? EntradaBytes, long? SalidaBytes, long? ErroresEntrada, long? ErroresSalida, long? Descartes);

public sealed record SnmpMacData(int IfIndex, string Mac, int? Vlan);

public sealed record SnmpArpData(string Mac, string Ip);

public sealed record SnmpVecinoData(int PuertoLocal, string? Sistema, string? Puerto);

/// <summary>Recolectores SNMP: su configuración y lo que reportan (sección 11.1).</summary>
public interface ISnmpCollectors
{
    /// <summary>Lo que debe consultar el agente; null si no tiene dispositivos asignados.</summary>
    Task<CollectorConfig?> ConfigAsync(AgentIdentity identity, CancellationToken cancellationToken);

    Task SaveResultAsync(AgentIdentity identity, SnmpResultadoData data, DateTime sampledAtUtc, CancellationToken cancellationToken);
}

/// <summary>Avisa a los recolectores conectados que su lista de dispositivos cambió.</summary>
public interface ICollectorConfigPublisher
{
    Task ConfigChangedAsync(int companyId, IEnumerable<int> agentIds, CancellationToken cancellationToken);
}

/// <summary>
/// Sella las claves SNMP para un agente: AES-256-GCM con una llave derivada (HKDF-SHA256) del hash de su credencial,
/// que el servidor guarda y el agente calcula. Así solo ese agente las abre, aunque la conexión no tuviera TLS
/// (desarrollo) o alguien leyera el mensaje en un registro. Formato: base64(nonce 12 | cifrado | etiqueta 16).
/// </summary>
public static class SnmpCredentialSeal
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("monitoreoti/snmp/v1");

    public static string Seal(string agentTokenHash, string plaintext)
    {
        var key = Key(agentTokenHash);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>Lo que hace el agente (aquí, para las pruebas): abre con su credencial.</summary>
    public static string Open(string agentCredential, string sealedValue)
    {
        var raw = Convert.FromBase64String(sealedValue);
        var key = Key(AgentTokens.Hash(agentCredential));
        var plain = new byte[raw.Length - 28];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, plain.Length), raw.AsSpan(raw.Length - 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Key(string agentTokenHash) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(agentTokenHash), 32, [], Info);
}
