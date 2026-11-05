using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Barrido de una subred para el inventario de direcciones IP (decisión 72): un ping a cada dirección pedida y, al final,
/// la tabla ARP de este equipo (los equipos de su misma red aparecen ahí aunque su firewall no conteste el ping).
/// El agente Node lo pide con "MonitoreoTI.AgentHost.exe ping": la petición llega por la entrada estándar y el resultado
/// sale en JSON por la salida estándar.
/// </summary>
public static class IpSweep
{
    /// <summary>Lo mismo que acepta el servidor: hasta una /22.</summary>
    private const int MaxAddresses = 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        SweepRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SweepRequest>(await input.ReadToEndAsync(), Json);
        }
        catch (JsonException)
        {
            request = null;
        }
        var addresses = new List<IPAddress>();
        foreach (var text in request?.Ips ?? [])
            if (IPAddress.TryParse(text, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                addresses.Add(address);
        if (request is null || addresses.Count == 0 || addresses.Count > MaxAddresses)
        {
            await output.WriteAsync(JsonSerializer.Serialize(new SweepResult { Ok = false, Error = "Petición de barrido inválida." }, Json));
            return 2;
        }

        var timeout = Math.Clamp(request.TimeoutMs ?? 800, 200, 5000);
        var parallel = Math.Clamp(request.Paralelo ?? 64, 1, 128);
        var answered = new List<string>();
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(addresses.Select(async address =>
        {
            await gate.WaitAsync();
            try
            {
                if (await PingAsync(address, timeout))
                    lock (answered)
                        answered.Add(address.ToString());
            }
            finally
            {
                gate.Release();
            }
        }));

        var wanted = addresses.Select(a => a.ToString()).ToHashSet();
        var arp = LocalArp().Where(a => wanted.Contains(a.Ip)).ToList();
        // Nombres solo de lo que se vio (respondió o está en la ARP): un registro viejo del DNS no inventa equipos.
        var seen = answered.Concat(arp.Select(a => a.Ip)).Distinct().Select(IPAddress.Parse).ToList();
        var names = request.Nombres == false ? [] : await HostNames.ResolveAsync(seen, timeout, Math.Min(parallel, 32));
        await output.WriteAsync(JsonSerializer.Serialize(new SweepResult { Ok = true, Responden = answered, Arp = arp, Nombres = names }, Json));
        return 0;
    }

    private static async Task<bool> PingAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException)
        {
            return false;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    /// <summary>
    /// Tabla ARP de Windows (GetIpNetTable): no depende del idioma como la salida de "arp -a". Cada fila (MIB_IPNETROW)
    /// mide 24 bytes: índice, largo de la MAC, MAC (8), dirección (4, en orden de red) y tipo (2 = inválida).
    /// </summary>
    private static List<SnmpArp> LocalArp()
    {
        var entries = new List<SnmpArp>();
        if (!OperatingSystem.IsWindows())
            return entries;
        var size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return entries;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0)
                return entries;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + (i * 24);
                var macLength = Marshal.ReadInt32(row, 4);
                var type = Marshal.ReadInt32(row, 20);
                if (macLength != 6 || type == 2)
                    continue;
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                var ip = new byte[4];
                Marshal.Copy(row + 16, ip, 0, 4);
                entries.Add(new SnmpArp { Ip = new IPAddress(ip).ToString(), Mac = string.Join(':', mac.Select(b => b.ToString("X2"))) });
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return entries;
    }
}

public sealed class SweepRequest
{
    public List<string>? Ips { get; set; }
    public int? TimeoutMs { get; set; }
    public int? Paralelo { get; set; }

    /// <summary>Averiguar el nombre de los equipos vistos (NetBIOS y DNS); sin indicar, sí.</summary>
    public bool? Nombres { get; set; }
}

public sealed class SweepResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<string>? Responden { get; set; }
    public List<SnmpArp>? Arp { get; set; }
    public List<HostName>? Nombres { get; set; }
}
