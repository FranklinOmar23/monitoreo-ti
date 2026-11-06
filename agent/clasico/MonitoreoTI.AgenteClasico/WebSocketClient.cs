using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Cliente WebSocket mínimo (RFC 6455) sobre TCP + TLS 1.2: el ClientWebSocket del Framework no funciona en
    /// Windows 7. Texto (el protocolo del agente es JSON) y binario (pantalla y archivos de las sesiones remotas), con
    /// ping/pong y cierre.
    /// </summary>
    public sealed class WebSocketClient : IDisposable
    {
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessageBytes = 16 * 1024 * 1024;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();
        private TcpClient _tcp;
        private Stream _stream;

        public static async Task<WebSocketClient> ConnectAsync(Uri uri, string bearer, TimeSpan timeout)
        {
            var client = new WebSocketClient();
            try
            {
                await client.OpenAsync(uri, bearer, timeout).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private async Task OpenAsync(Uri uri, string bearer, TimeSpan timeout)
        {
            var secure = uri.Scheme == "wss" || uri.Scheme == "https";
            var port = uri.IsDefaultPort ? (secure ? 443 : 80) : uri.Port;
            _tcp = new TcpClient { NoDelay = true };
            var connect = _tcp.ConnectAsync(uri.Host, port);
            if (await Task.WhenAny(connect, Task.Delay(timeout)).ConfigureAwait(false) != connect)
                throw new IOException($"{uri.Host}:{port} no respondió a tiempo.");
            await connect.ConfigureAwait(false);

            Stream stream = _tcp.GetStream();
            if (secure)
            {
                var ssl = new SslStream(stream, false, Tls.Validate);
                Tls.ExpectedHost = uri.Host;
                try
                {
                    await ssl.AuthenticateAsClientAsync(uri.Host, null, SslProtocols.Tls12, false).ConfigureAwait(false);
                }
                catch (AuthenticationException) when (Tls.LastProblem != null)
                {
                    throw new IOException($"No se confió en el servidor: {Tls.LastProblem}.");
                }
                stream = ssl;
            }
            _stream = stream;

            var keyBytes = new byte[16];
            _random.GetBytes(keyBytes);
            var key = Convert.ToBase64String(keyBytes);
            var request = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n" +
                (string.IsNullOrEmpty(bearer) ? "" : $"Authorization: Bearer {bearer}\r\n") + "\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

            var headers = await ReadHeadersAsync().ConfigureAwait(false);
            var status = headers.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            if (!status.Contains(" 101 "))
                throw new IOException($"El servidor no aceptó la conexión: {status}");
            string expected;
            using (var sha1 = SHA1.Create())
                expected = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + Guid)));
            if (headers.IndexOf("Sec-WebSocket-Accept: " + expected, StringComparison.OrdinalIgnoreCase) < 0)
                throw new IOException("Respuesta WebSocket inválida del servidor.");
        }

        private async Task<string> ReadHeadersAsync()
        {
            var buffer = new StringBuilder();
            var one = new byte[1];
            while (buffer.Length < 16 * 1024)
            {
                if (await _stream.ReadAsync(one, 0, 1).ConfigureAwait(false) != 1)
                    throw new IOException("El servidor cerró la conexión.");
                buffer.Append((char)one[0]);
                if (buffer.Length >= 4 && buffer.ToString(buffer.Length - 4, 4) == "\r\n\r\n")
                    return buffer.ToString();
            }
            throw new IOException("Encabezados demasiado largos.");
        }

        public Task SendTextAsync(string text) => SendFrameAsync(0x1, Encoding.UTF8.GetBytes(text));

        public Task SendBinaryAsync(byte[] data) => SendFrameAsync(0x2, data);

        private async Task SendFrameAsync(byte opcode, byte[] payload)
        {
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var frame = BuildFrame(opcode, payload, NextMask());
                await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private byte[] NextMask()
        {
            var mask = new byte[4];
            _random.GetBytes(mask);
            return mask;
        }

        /// <summary>Marco del cliente: FIN, opcode, largo y carga enmascarada (el cliente siempre enmascara).</summary>
        public static byte[] BuildFrame(byte opcode, byte[] payload, byte[] mask)
        {
            var length = payload.Length;
            var header = length < 126 ? 2 : length <= ushort.MaxValue ? 4 : 10;
            var frame = new byte[header + 4 + length];
            frame[0] = (byte)(0x80 | opcode);
            if (length < 126)
                frame[1] = (byte)(0x80 | length);
            else if (length <= ushort.MaxValue)
            {
                frame[1] = 0x80 | 126;
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)length;
            }
            else
            {
                frame[1] = 0x80 | 127;
                for (var i = 0; i < 8; i++)
                    frame[2 + i] = (byte)((long)length >> (8 * (7 - i)));
            }
            Buffer.BlockCopy(mask, 0, frame, header, 4);
            for (var i = 0; i < length; i++)
                frame[header + 4 + i] = (byte)(payload[i] ^ mask[i % 4]);
            return frame;
        }

        /// <summary>El siguiente mensaje de texto; null si el servidor cerró (los binarios se leen como texto).</summary>
        public async Task<string> ReceiveTextAsync()
        {
            var received = await ReceiveAsync().ConfigureAwait(false);
            return received == null ? null : Encoding.UTF8.GetString(received.Data);
        }

        /// <summary>El siguiente mensaje (texto o binario); null si el servidor cerró.</summary>
        public async Task<WebSocketMessage> ReceiveAsync()
        {
            var message = new MemoryStream();
            var binary = false;
            while (true)
            {
                var head = await ReadExactAsync(2).ConfigureAwait(false);
                if (head == null)
                    return null;
                var fin = (head[0] & 0x80) != 0;
                var opcode = head[0] & 0x0F;
                var masked = (head[1] & 0x80) != 0;
                long length = head[1] & 0x7F;
                if (length == 126)
                {
                    var ext = await ReadExactAsync(2).ConfigureAwait(false);
                    if (ext == null)
                        return null;
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    var ext = await ReadExactAsync(8).ConfigureAwait(false);
                    if (ext == null)
                        return null;
                    length = 0;
                    for (var i = 0; i < 8; i++)
                        length = (length << 8) | ext[i];
                }
                if (length > MaxMessageBytes || message.Length + length > MaxMessageBytes)
                    throw new IOException("Mensaje demasiado grande.");
                byte[] mask = null;
                if (masked)
                {
                    mask = await ReadExactAsync(4).ConfigureAwait(false);
                    if (mask == null)
                        return null;
                }
                var payload = length == 0 ? new byte[0] : await ReadExactAsync((int)length).ConfigureAwait(false);
                if (payload == null)
                    return null;
                if (mask != null)
                    for (var i = 0; i < payload.Length; i++)
                        payload[i] ^= mask[i % 4];

                switch (opcode)
                {
                    case 0x8: // cierre
                        try
                        {
                            await SendFrameAsync(0x8, new byte[0]).ConfigureAwait(false);
                        }
                        catch (IOException)
                        {
                        }
                        return null;
                    case 0x9: // ping
                        await SendFrameAsync(0xA, payload).ConfigureAwait(false);
                        continue;
                    case 0xA: // pong
                        continue;
                    case 0x1:
                    case 0x0:
                    case 0x2:
                        if (opcode == 0x2)
                            binary = true;
                        message.Write(payload, 0, payload.Length);
                        if (fin)
                            return new WebSocketMessage(binary, message.ToArray());
                        continue;
                    default:
                        throw new IOException($"Marco WebSocket desconocido ({opcode}).");
                }
            }
        }

        private async Task<byte[]> ReadExactAsync(int count)
        {
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = await _stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n == 0)
                    return null;
                read += n;
            }
            return buffer;
        }

        public void Close()
        {
            try
            {
                SendFrameAsync(0x8, new byte[0]).Wait(2000);
            }
            catch (Exception)
            {
                // Ya estaba cerrada.
            }
            Dispose();
        }

        public void Dispose()
        {
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
            }
            _tcp?.Close();
        }
    }

    /// <summary>Un mensaje recibido: binario (opcode 2) o texto en UTF-8.</summary>
    public sealed class WebSocketMessage
    {
        public WebSocketMessage(bool binary, byte[] data)
        {
            Binary = binary;
            Data = data;
        }

        public bool Binary { get; }
        public byte[] Data { get; }
        public string Text => Encoding.UTF8.GetString(Data);
    }
}
