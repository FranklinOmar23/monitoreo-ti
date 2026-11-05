using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Nombre del equipo que está en una IP (decisión 75), para el inventario de direcciones: primero el que da el propio
/// equipo por NetBIOS (en una red Windows casi todas las PCs responden, aunque bloqueen el ping) y, si no, el registro
/// inverso del DNS. Solo se pregunta por las IP que se vieron en el barrido.
/// </summary>
public static class HostNames
{
    private const int NetBiosPort = 137;

    public static async Task<List<HostName>> ResolveAsync(IEnumerable<IPAddress> addresses, int timeoutMs, int parallel)
    {
        var found = new List<HostName>();
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(addresses.Distinct().Select(async address =>
        {
            await gate.WaitAsync();
            try
            {
                var name = await NetBiosAsync(address, timeoutMs) is { } netbios
                    ? new HostName { Ip = address.ToString(), Nombre = netbios, Origen = "netbios" }
                    : await DnsAsync(address, timeoutMs) is { } dns
                        ? new HostName { Ip = address.ToString(), Nombre = dns, Origen = "dns" }
                        : null;
                if (name is not null)
                    lock (found)
                        found.Add(name);
            }
            finally
            {
                gate.Release();
            }
        }));
        return found;
    }

    /// <summary>Registro inverso del DNS; el nombre corto ("conta-pc03.caribe.local" → "conta-pc03").</summary>
    private static async Task<string?> DnsAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            if (await Task.WhenAny(lookup, Task.Delay(timeoutMs)) != lookup)
                return null;
            var host = (await lookup).HostName;
            if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
                return null;
            return host.Split('.')[0];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Consulta NBSTAT (RFC 1002) por UDP 137: el equipo responde con sus nombres; se toma el de la máquina.</summary>
    private static async Task<string?> NetBiosAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var timeout = new CancellationTokenSource(timeoutMs);
            await udp.SendAsync(NodeStatusRequest(), new IPEndPoint(address, NetBiosPort), timeout.Token);
            var reply = await udp.ReceiveAsync(timeout.Token);
            return ParseNodeStatus(reply.Buffer);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Pedido "¿qué nombres tienes?" con el nombre comodín "*".</summary>
    public static byte[] NodeStatusRequest()
    {
        var packet = new List<byte>
        {
            0x13, 0x37, // id de la transacción
            0x00, 0x00, // consulta
            0x00, 0x01, // una pregunta
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x20, // largo del nombre codificado
        };
        // "*" y 15 ceros, cada byte en dos letras (A + mitad alta, A + mitad baja).
        var raw = new byte[16];
        raw[0] = (byte)'*';
        foreach (var b in raw)
        {
            packet.Add((byte)('A' + (b >> 4)));
            packet.Add((byte)('A' + (b & 0x0F)));
        }
        packet.AddRange([0x00, 0x00, 0x21, 0x00, 0x01]); // fin del nombre, tipo NBSTAT, clase IN
        return [.. packet];
    }

    /// <summary>
    /// La respuesta trae una lista de nombres de 18 bytes (15 de nombre, sufijo y banderas). El de la máquina es el
    /// único (no de grupo) con sufijo 0x00; null si no hay.
    /// </summary>
    public static string? ParseNodeStatus(byte[] reply)
    {
        // Encabezado (12) + nombre de la respuesta (34) + tipo, clase, TTL y largo de los datos (10).
        const int countAt = 12 + 34 + 10;
        if (reply.Length <= countAt)
            return null;
        int count = reply[countAt];
        for (var i = 0; i < count; i++)
        {
            var at = countAt + 1 + i * 18;
            if (at + 18 > reply.Length)
                return null;
            var suffix = reply[at + 15];
            var group = (reply[at + 16] & 0x80) != 0;
            if (suffix != 0x00 || group)
                continue;
            var name = Encoding.ASCII.GetString(reply, at, 15).TrimEnd(' ', '\0');
            return name.Length == 0 || name.Any(c => c < 0x20 || c > 0x7E) ? null : name;
        }
        return null;
    }
}

public sealed class HostName
{
    public string Ip { get; set; } = "";
    public string Nombre { get; set; } = "";

    /// <summary>netbios o dns.</summary>
    public string Origen { get; set; } = "";
}
