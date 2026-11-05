using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MonitoreoTI.AgenteClasico;
using Xunit;

namespace MonitoreoTI.AgenteClasico.Tests
{
    public class ClassicAgentTests
    {
        [Fact]
        public void Frames_from_the_client_are_masked_with_the_right_length_header()
        {
            var mask = new byte[] { 1, 2, 3, 4 };
            var small = WebSocketClient.BuildFrame(0x1, Encoding.UTF8.GetBytes("hola"), mask);
            Assert.Equal(0x81, small[0]);
            Assert.Equal(0x80 | 4, small[1]);
            Assert.Equal("hola", Encoding.UTF8.GetString(small.Skip(6).Select((b, i) => (byte)(b ^ mask[i % 4])).ToArray()));

            var medium = WebSocketClient.BuildFrame(0x1, new byte[300], mask);
            Assert.Equal(0x80 | 126, medium[1]);
            Assert.Equal(300, (medium[2] << 8) | medium[3]);

            var large = WebSocketClient.BuildFrame(0x1, new byte[70000], mask);
            Assert.Equal(0x80 | 127, large[1]);
            Assert.Equal(70000L, large.Skip(2).Take(8).Aggregate(0L, (acc, b) => (acc << 8) | b));
        }

        /// <summary>Un servidor WebSocket mínimo: valida el saludo, manda un ping y devuelve el texto recibido.</summary>
        [Fact]
        public async Task The_client_handshakes_with_the_bearer_answers_ping_and_exchanges_text()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string seenHeaders = null;
            var server = Task.Run(async () =>
            {
                using (var tcp = await listener.AcceptTcpClientAsync())
                using (var stream = tcp.GetStream())
                {
                    var headers = new StringBuilder();
                    var one = new byte[1];
                    while (!headers.ToString().EndsWith("\r\n\r\n"))
                    {
                        await stream.ReadAsync(one, 0, 1);
                        headers.Append((char)one[0]);
                    }
                    seenHeaders = headers.ToString();
                    var key = seenHeaders.Split(new[] { "\r\n" }, StringSplitOptions.None)
                        .First(l => l.StartsWith("Sec-WebSocket-Key:")).Substring(18).Trim();
                    string accept;
                    using (var sha1 = SHA1.Create())
                        accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n");
                    await stream.WriteAsync(reply, 0, reply.Length);

                    // Ping (el cliente debe contestar pong) y un mensaje en dos fragmentos.
                    await stream.WriteAsync(new byte[] { 0x89, 0x01, 0x2A }, 0, 3);
                    var pong = new byte[2 + 4 + 1];
                    await ReadExact(stream, pong);
                    Assert.Equal(0x8A, pong[0]);
                    var part1 = Encoding.UTF8.GetBytes("{\"tipo\":");
                    var part2 = Encoding.UTF8.GetBytes("\"bienvenida\"}");
                    await stream.WriteAsync(new[] { (byte)0x01, (byte)part1.Length }.Concat(part1).ToArray(), 0, 2 + part1.Length);
                    await stream.WriteAsync(new[] { (byte)0x80, (byte)part2.Length }.Concat(part2).ToArray(), 0, 2 + part2.Length);

                    // Lo que manda el cliente (enmascarado) vuelve tal cual.
                    var head = new byte[2];
                    await ReadExact(stream, head);
                    var length = head[1] & 0x7F;
                    var mask = new byte[4];
                    await ReadExact(stream, mask);
                    var payload = new byte[length];
                    await ReadExact(stream, payload);
                    var text = payload.Select((b, i) => (byte)(b ^ mask[i % 4])).ToArray();
                    await stream.WriteAsync(new[] { (byte)0x81, (byte)text.Length }.Concat(text).ToArray(), 0, 2 + text.Length);
                    await stream.WriteAsync(new byte[] { 0x88, 0x00 }, 0, 2);
                    await Task.Delay(200);
                }
            });

            using (var client = await WebSocketClient.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/agent/ws"), "7.credencial", TimeSpan.FromSeconds(5)))
            {
                Assert.Equal("{\"tipo\":\"bienvenida\"}", await client.ReceiveTextAsync());
                await client.SendTextAsync("ñandú");
                Assert.Equal("ñandú", await client.ReceiveTextAsync());
                Assert.Null(await client.ReceiveTextAsync());
            }
            await server;
            listener.Stop();
            Assert.Contains("GET /agent/ws HTTP/1.1", seenHeaders);
            Assert.Contains("Authorization: Bearer 7.credencial", seenHeaders);
        }

        private static async Task ReadExact(NetworkStream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
                read += await stream.ReadAsync(buffer, read, buffer.Length - read);
        }

        [Fact]
        public void The_signed_payload_is_the_same_text_the_server_signs()
        {
            var payload = Commands.SignedPayload(15, 42, "script", "powershell", 300, "2026-11-05T15:00:00.000Z", "Get-Date");
            var lines = payload.Split('\n');
            Assert.Equal(new[] { "monitoreoti/comando/v1", "15", "42", "script", "powershell", "300", "2026-11-05T15:00:00.000Z" }, lines.Take(7));
            Assert.Equal(64, lines[7].Length);
            Assert.Equal(lines[7], lines[7].ToLowerInvariant());
        }

        /// <summary>Firma P-256 en IEEE P1363 y llave en SPKI, como las del servidor.</summary>
        [Fact]
        public void Verifies_company_signatures_and_rejects_altered_ones()
        {
            using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var p = ecdsa.ExportParameters(false);
                var header = new byte[] { 0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04 };
                var spki = Convert.ToBase64String(header.Concat(p.Q.X).Concat(p.Q.Y).ToArray());
                var payload = Commands.SignedPayload(1, 2, "script", "cmd", 60, "2030-01-01T00:00:00Z", "dir");
                var signature = Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256));

                Assert.True(Commands.Verify(spki, payload, signature));
                Assert.False(Commands.Verify(spki, Commands.SignedPayload(1, 2, "script", "cmd", 60, "2030-01-01T00:00:00Z", "del"), signature));
                Assert.False(Commands.Verify(spki, payload, Convert.ToBase64String(new byte[64])));
                Assert.False(Commands.Verify("no-es-base64", payload, signature));
            }
        }

        [Fact]
        public void Reads_installer_options_in_both_styles()
        {
            var args = new[] { "instalar", "/SERVER=https://192.168.3.115:5127", "--token", "1.abc" };
            Assert.Equal("https://192.168.3.115:5127", Program.Option(args, "SERVER"));
            Assert.Equal("1.abc", Program.Option(args, "TOKEN"));
            Assert.Null(Program.Option(args, "OTRO"));
        }

        [Fact]
        public void Json_reads_objects_and_numbers_from_the_server()
        {
            var d = Json.Read("{\"tipo\":\"bienvenida\",\"datos\":{\"heartbeat_s\":30,\"llave_empresa\":null}}");
            Assert.Equal("bienvenida", d.Str("tipo"));
            Assert.Equal(30, d.Obj("datos").Int("heartbeat_s"));
            Assert.Null(d.Obj("datos").Str("llave_empresa"));
            Assert.Null(Json.Read("no es json"));
        }
    }
}
