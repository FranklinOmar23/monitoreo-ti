using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Consulta SNMP de solo lectura de un dispositivo, para el agente recolector (sección 11.1): SNMPv3 authPriv, o v2c
/// con comunidad solo para impresoras que no tienen v3 (decisión 47).
/// El agente Node la pide con "MonitoreoTI.AgentHost.exe snmp": la petición (con las claves) llega por la entrada
/// estándar y el resultado sale en JSON por la salida estándar. Las claves nunca se escriben en ningún lado.
/// </summary>
public static class SnmpQuery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        SnmpRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SnmpRequest>(await input.ReadToEndAsync(), Json);
        }
        catch (JsonException)
        {
            request = null;
        }
        var v2c = string.Equals(request?.Version, "v2c", StringComparison.OrdinalIgnoreCase);
        if (request is null || !IPAddress.TryParse(request.Ip, out var address)
            || (v2c ? string.IsNullOrEmpty(request.Comunidad)
                    : string.IsNullOrEmpty(request.Usuario) || (request.ClaveAutenticacion?.Length ?? 0) < 8 || (request.ClavePrivacidad?.Length ?? 0) < 8))
        {
            await output.WriteAsync(JsonSerializer.Serialize(new SnmpResult { Ok = false, Error = "Petición SNMP inválida." }, Json));
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SnmpResult result;
        try
        {
            result = await QueryAsync(new IPEndPoint(address, request.Puerto ?? 161), request, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.TimeoutException or Lextm.SharpSnmpLib.Messaging.TimeoutException
            or System.Net.Sockets.SocketException)
        {
            result = new SnmpResult { Ok = false, Error = "El dispositivo no respondió por SNMP." };
        }
        catch (Exception ex)
        {
            // Mensajes de la librería: usuario desconocido, firma o descifrado inválido (claves o protocolo equivocados).
            result = new SnmpResult { Ok = false, Error = $"SNMP rechazó la consulta: {ex.Message}" };
        }

        await output.WriteAsync(JsonSerializer.Serialize(result, Json));
        return 0;
    }

    private static async Task<SnmpResult> QueryAsync(IPEndPoint endpoint, SnmpRequest request, CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs ?? 5000, 1000, 30000));
        var wants = request.Consultas is { Length: > 0 } list ? list.ToHashSet(StringComparer.OrdinalIgnoreCase) : DefaultQueries;
        if (string.Equals(request.Version, "v2c", StringComparison.OrdinalIgnoreCase))
            return await RunAsync(new Session(endpoint, VersionCode.V2, new OctetString(request.Comunidad!), null, null, wait), wants, ct);

        IAuthenticationProvider auth = request.Autenticacion?.ToLowerInvariant() switch
        {
            "sha256" => new SHA256AuthenticationProvider(new OctetString(request.ClaveAutenticacion!)),
            // SHA-1 es lo que soporta casi todo switch con SNMPv3 (sección 11.1); el que admita SHA-256 lo indica.
#pragma warning disable CS0618
            _ => new SHA1AuthenticationProvider(new OctetString(request.ClaveAutenticacion!)),
#pragma warning restore CS0618
        };
        IPrivacyProvider privacy = request.Privacidad?.ToLowerInvariant() switch
        {
            "aes256" => new AES256PrivacyProvider(new OctetString(request.ClavePrivacidad!), auth),
            _ => new AESPrivacyProvider(new OctetString(request.ClavePrivacidad!), auth),
        };
        var user = new OctetString(request.Usuario!);

        // Descubrimiento del motor SNMP (engineID, tiempo) que exige v3 antes de la primera consulta.
        ISnmpMessage report;
        using (var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            discoveryTimeout.CancelAfter(wait);
            var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
            report = await discovery.GetResponseAsync(endpoint, discoveryTimeout.Token);
        }
        return await RunAsync(new Session(endpoint, VersionCode.V3, user, privacy, report, wait), wants, ct);
    }

    private static async Task<SnmpResult> RunAsync(Session session, HashSet<string> wants, CancellationToken ct)
    {
        {
            var result = new SnmpResult { Ok = true, Sistema = await session.SystemAsync(ct) };
            if (wants.Contains("interfaces"))
                result.Interfaces = await session.InterfacesAsync(ct);
            if (wants.Contains("mac"))
                result.Mac = await session.MacTableAsync(ct);
            if (wants.Contains("arp"))
                result.Arp = await session.ArpAsync(ct);
            if (wants.Contains("lldp"))
                result.Lldp = await session.LldpAsync(ct);
            if (wants.Contains("consumibles"))
                result.Consumibles = await session.SuppliesAsync(ct);
            return result;
        }
    }

    private static readonly HashSet<string> DefaultQueries = new(["interfaces", "mac", "arp", "lldp"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Consultas sobre un dispositivo: v3 (ya descubierto, con usuario y privacidad) o v2c (la "identidad" es la comunidad).
    /// </summary>
    private sealed class Session(IPEndPoint endpoint, VersionCode version, OctetString user, IPrivacyProvider? privacy, ISnmpMessage? report, TimeSpan wait)
    {
        private const string SysDescr = "1.3.6.1.2.1.1.1.0";
        private const string SysUpTime = "1.3.6.1.2.1.1.3.0";
        private const string SysName = "1.3.6.1.2.1.1.5.0";

        public async Task<SnmpSystem> SystemAsync(CancellationToken ct)
        {
            var variables = new List<Variable> { new(new ObjectIdentifier(SysDescr)), new(new ObjectIdentifier(SysName)), new(new ObjectIdentifier(SysUpTime)) };
            ISnmpMessage request = version == VersionCode.V3
                ? new GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, OctetString.Empty,
                    variables, privacy!, Messenger.MaxMessageSize, report!)
                : new GetRequestMessage(Messenger.NextRequestId, version, user, variables);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(wait);
            var reply = await request.GetResponseAsync(endpoint, timeout.Token);
            if (reply.Pdu().ErrorStatus.ToInt32() != 0)
                throw new InvalidOperationException($"error {reply.Pdu().ErrorStatus.ToInt32()} al leer el sistema");

            var values = reply.Pdu().Variables.ToDictionary(v => v.Id.ToString(), v => v.Data);
            return new SnmpSystem
            {
                Descripcion = Text(values.GetValueOrDefault(SysDescr)),
                Nombre = Text(values.GetValueOrDefault(SysName)),
                UptimeSegundos = values.GetValueOrDefault(SysUpTime) is TimeTicks ticks ? (long)(ticks.ToUInt32() / 100) : null,
            };
        }

        public async Task<List<SnmpInterface>> InterfacesAsync(CancellationToken ct)
        {
            var columns = new Dictionary<string, string>
            {
                ["tipo"] = "1.3.6.1.2.1.2.2.1.3",
                ["admin"] = "1.3.6.1.2.1.2.2.1.7",
                ["oper"] = "1.3.6.1.2.1.2.2.1.8",
                ["inDiscards"] = "1.3.6.1.2.1.2.2.1.13",
                ["inErrors"] = "1.3.6.1.2.1.2.2.1.14",
                ["outErrors"] = "1.3.6.1.2.1.2.2.1.20",
                ["name"] = "1.3.6.1.2.1.31.1.1.1.1",
                ["inOctets"] = "1.3.6.1.2.1.31.1.1.1.6",
                ["outOctets"] = "1.3.6.1.2.1.31.1.1.1.10",
                ["speed"] = "1.3.6.1.2.1.31.1.1.1.15",
                ["alias"] = "1.3.6.1.2.1.31.1.1.1.18",
            };
            var table = new Dictionary<int, SnmpInterface>();
            SnmpInterface Row(int index) => table.TryGetValue(index, out var row) ? row : table[index] = new SnmpInterface { Indice = index };

            foreach (var (column, oid) in columns)
            {
                foreach (var (suffix, data) in await WalkAsync(oid, ct))
                {
                    if (suffix.Length != 1)
                        continue;
                    var row = Row((int)suffix[0]);
                    switch (column)
                    {
                        case "tipo": row.Tipo = Int(data); break;
                        case "admin": row.Admin = Int(data) == 1 ? "up" : "down"; break;
                        case "oper": row.Operativo = Int(data) == 1 ? "up" : "down"; break;
                        case "inDiscards": row.Descartes = Long(data); break;
                        case "inErrors": row.ErroresEntrada = Long(data); break;
                        case "outErrors": row.ErroresSalida = Long(data); break;
                        case "name": row.Nombre = Text(data); break;
                        case "inOctets": row.EntradaBytes = Long(data); break;
                        case "outOctets": row.SalidaBytes = Long(data); break;
                        case "speed": row.VelocidadMbps = Long(data); break;
                        case "alias": row.Alias = Text(data); break;
                    }
                }
            }
            return table.Values.OrderBy(i => i.Indice).ToList();
        }

        /// <summary>MAC por puerto: Q-BRIDGE (con VLAN) o, si el equipo no la tiene, BRIDGE; el puerto del puente se pasa a ifIndex.</summary>
        public async Task<List<SnmpMac>> MacTableAsync(CancellationToken ct)
        {
            var bridgeToIf = new Dictionary<long, int>();
            foreach (var (suffix, data) in await WalkAsync("1.3.6.1.2.1.17.1.4.1.2", ct))
                if (suffix.Length == 1)
                    bridgeToIf[suffix[0]] = Int(data) ?? 0;

            var entries = new List<SnmpMac>();
            foreach (var (suffix, data) in await WalkAsync("1.3.6.1.2.1.17.7.1.2.2.1.2", ct))
                if (suffix.Length == 7 && Long(data) is long port and > 0)
                    entries.Add(new SnmpMac { Vlan = (int)suffix[0], Mac = Mac(suffix[1..]), IfIndex = bridgeToIf.GetValueOrDefault(port, (int)port) });

            if (entries.Count == 0)
            {
                foreach (var (suffix, data) in await WalkAsync("1.3.6.1.2.1.17.4.3.1.2", ct))
                    if (suffix.Length == 6 && Long(data) is long port and > 0)
                        entries.Add(new SnmpMac { Mac = Mac(suffix), IfIndex = bridgeToIf.GetValueOrDefault(port, (int)port) });
            }
            return entries;
        }

        public async Task<List<SnmpArp>> ArpAsync(CancellationToken ct)
        {
            var entries = new List<SnmpArp>();
            foreach (var (suffix, data) in await WalkAsync("1.3.6.1.2.1.4.22.1.2", ct))
                if (suffix.Length == 5 && data is OctetString octets && octets.GetRaw().Length == 6)
                    entries.Add(new SnmpArp { Ip = string.Join('.', suffix[1..]), Mac = Mac(octets.GetRaw().Select(b => (uint)b).ToArray()), IfIndex = (int)suffix[0] });
            return entries;
        }

        public async Task<List<SnmpNeighbor>> LldpAsync(CancellationToken ct)
        {
            var names = await WalkAsync("1.0.8802.1.1.2.1.4.1.1.9", ct);
            var ports = (await WalkAsync("1.0.8802.1.1.2.1.4.1.1.7", ct)).ToDictionary(p => string.Join('.', p.Suffix), p => p.Data);
            return names.Where(n => n.Suffix.Length == 3)
                .Select(n => new SnmpNeighbor
                {
                    PuertoLocal = (int)n.Suffix[1],
                    Sistema = Text(n.Data),
                    Puerto = ports.TryGetValue(string.Join('.', n.Suffix), out var port) ? PortId(port) : null,
                })
                .ToList();
        }

        public async Task<List<SnmpSupply>> SuppliesAsync(CancellationToken ct)
        {
            var descriptions = (await WalkAsync("1.3.6.1.2.1.43.11.1.1.6", ct)).ToDictionary(x => string.Join('.', x.Suffix), x => Text(x.Data));
            var max = (await WalkAsync("1.3.6.1.2.1.43.11.1.1.8", ct)).ToDictionary(x => string.Join('.', x.Suffix), x => Int(x.Data));
            return (await WalkAsync("1.3.6.1.2.1.43.11.1.1.9", ct))
                .Select(x => string.Join('.', x.Suffix) is var key ? new SnmpSupply
                {
                    Indice = key,
                    Descripcion = descriptions.GetValueOrDefault(key),
                    Nivel = Int(x.Data),
                    Maximo = max.GetValueOrDefault(key),
                } : null!)
                .ToList();
        }

        private async Task<List<(uint[] Suffix, ISnmpData Data)>> WalkAsync(string oid, CancellationToken ct)
        {
            var root = new ObjectIdentifier(oid);
            var rows = new List<Variable>();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(wait * 6);
                // En v2c la librería arma la petición con la comunidad cuando no hay "report" (privacidad y report van null).
                await Messenger.BulkWalkAsync(version, endpoint, user, OctetString.Empty, root, rows, 20, WalkMode.WithinSubtree, privacy!, report!, timeout.Token);
            }
            catch (ErrorException)
            {
                // La tabla no existe en este equipo (por ejemplo, sin LLDP): se informa vacía.
            }
            var prefix = root.ToNumerical();
            return rows
                .Where(v => v.Data is not NoSuchInstance and not NoSuchObject and not EndOfMibView)
                .Select(v => (v.Id.ToNumerical()[prefix.Length..], v.Data))
                .ToList();
        }
    }

    // ------------------------------------------------------------------ conversiones

    private static string? Text(ISnmpData? data) => data switch
    {
        OctetString s => s.ToString().TrimEnd('\0').Trim() is { Length: > 0 } text ? text : null,
        null or NoSuchInstance or NoSuchObject or EndOfMibView => null,
        _ => data.ToString(),
    };

    private static int? Int(ISnmpData? data) => data switch
    {
        Integer32 i => i.ToInt32(),
        Gauge32 g => (int)Math.Min(g.ToUInt32(), int.MaxValue),
        _ => null,
    };

    private static long? Long(ISnmpData? data) => data switch
    {
        Counter64 c => (long)Math.Min(c.ToUInt64(), long.MaxValue),
        Counter32 c => c.ToUInt32(),
        Gauge32 g => g.ToUInt32(),
        Integer32 i => i.ToInt32(),
        _ => null,
    };

    private static string Mac(uint[] bytes) => string.Join(':', bytes.Select(b => ((byte)b).ToString("X2")));

    /// <summary>El id de puerto LLDP suele ser un nombre ("Gi1/0/48"); si son bytes (una MAC), se muestra en hexadecimal.</summary>
    private static string? PortId(ISnmpData data)
    {
        if (data is not OctetString octets)
            return Text(data);
        var raw = octets.GetRaw();
        return raw.All(b => b is >= 0x20 and < 0x7F) ? Encoding.ASCII.GetString(raw).Trim() : Mac(raw.Select(b => (uint)b).ToArray());
    }
}

public sealed class SnmpRequest
{
    public string? Ip { get; set; }
    public int? Puerto { get; set; }
    /// <summary>v3 (por defecto) o v2c (solo impresoras).</summary>
    public string? Version { get; set; }
    /// <summary>Comunidad de solo lectura, para v2c.</summary>
    public string? Comunidad { get; set; }
    public string? Usuario { get; set; }
    public string? ClaveAutenticacion { get; set; }
    public string? ClavePrivacidad { get; set; }
    /// <summary>sha (por defecto) o sha256.</summary>
    public string? Autenticacion { get; set; }
    /// <summary>aes (AES-128, por defecto) o aes256.</summary>
    public string? Privacidad { get; set; }
    public int? TimeoutMs { get; set; }
    /// <summary>interfaces, mac, arp, lldp, consumibles. Sin indicar: las cuatro primeras (un switch o router).</summary>
    public string[]? Consultas { get; set; }
}

public sealed class SnmpResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public SnmpSystem? Sistema { get; set; }
    public List<SnmpInterface>? Interfaces { get; set; }
    public List<SnmpMac>? Mac { get; set; }
    public List<SnmpArp>? Arp { get; set; }
    public List<SnmpNeighbor>? Lldp { get; set; }
    public List<SnmpSupply>? Consumibles { get; set; }
}

public sealed class SnmpSystem
{
    public string? Descripcion { get; set; }
    public string? Nombre { get; set; }
    public long? UptimeSegundos { get; set; }
}

public sealed class SnmpInterface
{
    public int Indice { get; set; }
    public int? Tipo { get; set; }
    public string? Nombre { get; set; }
    public string? Alias { get; set; }
    public string? Admin { get; set; }
    public string? Operativo { get; set; }
    public long? VelocidadMbps { get; set; }
    public long? EntradaBytes { get; set; }
    public long? SalidaBytes { get; set; }
    public long? ErroresEntrada { get; set; }
    public long? ErroresSalida { get; set; }
    public long? Descartes { get; set; }
}

public sealed class SnmpMac
{
    public int IfIndex { get; set; }
    public string Mac { get; set; } = "";
    public int? Vlan { get; set; }
}

public sealed class SnmpArp
{
    public string Mac { get; set; } = "";
    public string Ip { get; set; } = "";

    /// <summary>Interfaz del equipo donde está la entrada (ipNetToMediaIfIndex): dice en qué VLAN se ve la IP.</summary>
    public int? IfIndex { get; set; }
}

public sealed class SnmpNeighbor
{
    public int PuertoLocal { get; set; }
    public string? Sistema { get; set; }
    public string? Puerto { get; set; }
}

public sealed class SnmpSupply
{
    public string Indice { get; set; } = "";
    public string? Descripcion { get; set; }
    public int? Nivel { get; set; }
    public int? Maximo { get; set; }
}
