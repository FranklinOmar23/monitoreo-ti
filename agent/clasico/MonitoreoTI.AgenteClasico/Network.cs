using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Confianza TLS: el certificado del servidor vale si Windows lo acepta o si su cadena termina en el certificado de
    /// Centinela (ca-confianza.pem, el mismo que usa el agente normal). Windows 7 necesita TLS 1.2 activado a mano, y no
    /// reconoce las IP en los nombres alternativos del certificado (las da por "nombre que no coincide"): ese control
    /// se hace aquí, leyendo la extensión.
    /// </summary>
    public static class Tls
    {
        private static X509Certificate2 _trustedRoot;

        /// <summary>Servidor al que se conecta el WebSocket (la validación de SslStream no recibe el nombre).</summary>
        public static string ExpectedHost { get; set; }

        /// <summary>Por qué se rechazó el último certificado (para el registro y el error de la instalación).</summary>
        public static string LastProblem { get; private set; }

        public static void Configure()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            _trustedRoot = LoadPem(Paths.TrustedCaFile);
            ServicePointManager.ServerCertificateValidationCallback = Validate;
        }

        /// <summary>Para las pruebas: la CA de confianza sin leer el archivo.</summary>
        public static void UseTrustedRoot(X509Certificate2 root) => _trustedRoot = root;

        public static X509Certificate2 LoadPem(string file)
        {
            try
            {
                if (!File.Exists(file))
                    return null;
                var text = File.ReadAllText(file);
                var start = text.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
                var end = text.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal);
                if (start < 0 || end < start)
                    return null;
                var base64 = text.Substring(start + 27, end - start - 27).Replace("\r", "").Replace("\n", "").Trim();
                return new X509Certificate2(Convert.FromBase64String(base64));
            }
            catch (Exception ex) when (ex is IOException || ex is FormatException || ex is System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }

        public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            LastProblem = null;
            if (errors == SslPolicyErrors.None)
                return true;
            var host = (sender as HttpWebRequest)?.RequestUri.Host ?? ExpectedHost;
            if (certificate == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
                return Reject("el servidor no presentó certificado");
            var cert = new X509Certificate2(certificate);
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 && !HostMatches(cert, host))
                return Reject($"el certificado del servidor no incluye {host} (tiene: {string.Join(", ", SubjectAltNames(cert))})");
            if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
                return true;
            if (_trustedRoot == null)
                return Reject($"falta el certificado de Centinela ({Paths.TrustedCaFile}): copie MonitoreoTI-CA.pem junto al agente y vuelva a instalar");

            // En .NET 4.5.2 X509Chain no es IDisposable.
            var custom = new X509Chain();
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            custom.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            custom.ChainPolicy.ExtraStore.Add(_trustedRoot);
            if (!custom.Build(cert))
                return Reject("la cadena del certificado no es válida: " +
                    string.Join("; ", custom.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0)));
            var root = custom.ChainElements[custom.ChainElements.Count - 1].Certificate;
            return root.Thumbprint == _trustedRoot.Thumbprint || Reject($"el certificado del servidor lo emitió {root.Subject}, no la CA de Centinela");
        }

        private static bool Reject(string problem)
        {
            LastProblem = problem;
            Log.Error($"Certificado del servidor rechazado: {problem}.");
            return false;
        }

        /// <summary>¿El host (IP o nombre) está en los nombres alternativos del certificado?</summary>
        public static bool HostMatches(X509Certificate2 cert, string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            IPAddress ip;
            var isIp = IPAddress.TryParse(host, out ip);
            foreach (var name in SubjectAltNameEntries(cert))
            {
                if (isIp && name.Ip != null && name.Ip.Equals(ip))
                    return true;
                if (!isIp && name.Dns != null && DnsMatches(name.Dns, host))
                    return true;
            }
            return false;
        }

        private static bool DnsMatches(string pattern, string host)
        {
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var dot = host.IndexOf('.');
                return dot > 0 && string.Equals(pattern.Substring(1), host.Substring(dot), StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerable<string> SubjectAltNames(X509Certificate2 cert) =>
            SubjectAltNameEntries(cert).Select(n => n.Ip?.ToString() ?? n.Dns);

        private sealed class AltName
        {
            public IPAddress Ip;
            public string Dns;
        }

        /// <summary>
        /// Lee la extensión subjectAltName (2.5.29.17) en DER: una secuencia de nombres; [2] = DNS, [7] = IP (4 o 16 bytes).
        /// Se lee a mano porque el texto que da Windows depende del idioma.
        /// </summary>
        private static List<AltName> SubjectAltNameEntries(X509Certificate2 cert)
        {
            var result = new List<AltName>();
            var ext = cert.Extensions.Cast<X509Extension>().FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
            if (ext == null)
                return result;
            var data = ext.RawData;
            var pos = 0;
            if (data.Length < 2 || data[pos++] != 0x30)
                return result;
            var end = ReadLength(data, ref pos) + pos;
            while (pos < end && pos < data.Length)
            {
                var tag = data[pos++];
                var length = ReadLength(data, ref pos);
                if (length < 0 || pos + length > data.Length)
                    break;
                var value = new byte[length];
                Buffer.BlockCopy(data, pos, value, 0, length);
                pos += length;
                if (tag == 0x87 && (length == 4 || length == 16))
                    result.Add(new AltName { Ip = new IPAddress(value) });
                else if (tag == 0x82)
                    result.Add(new AltName { Dns = Encoding.ASCII.GetString(value) });
            }
            return result;
        }

        private static int ReadLength(byte[] data, ref int pos)
        {
            if (pos >= data.Length)
                return -1;
            int first = data[pos++];
            if (first < 0x80)
                return first;
            var count = first & 0x7F;
            if (count > 3 || pos + count > data.Length)
                return -1;
            var length = 0;
            for (var i = 0; i < count; i++)
                length = (length << 8) | data[pos++];
            return length;
        }
    }

    /// <summary>Registro del equipo con el token de la sucursal: devuelve la credencial permanente.</summary>
    public static class Enrollment
    {
        public sealed class RejectedException : Exception
        {
            public RejectedException() : base("El servidor rechazó el token de registro (inválido, vencido o ya usado).") { }
        }

        public static AgentConfig Enroll(string server, string token, Identity identity)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(new Uri(server), "/agent/register"));
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 30000;
            var body = Encoding.UTF8.GetBytes(Json.Write(new Dictionary<string, object>
            {
                ["enrollToken"] = token,
                ["hostname"] = identity.Hostname,
                ["macAddress"] = identity.MacAddress,
                ["ipAddress"] = identity.IpAddress,
                ["operatingSystem"] = identity.OperatingSystem,
                ["machineType"] = identity.MachineType,
                ["agentVersion"] = AgentInfo.Version,
            }));
            using (var stream = request.GetRequestStream())
                stream.Write(body, 0, body.Length);
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var d = Json.Read(reader.ReadToEnd());
                    var credential = d.Str("credential");
                    if (credential == null || d.Int("machineId") == null || d.Int("agentId") == null)
                        throw new InvalidOperationException("Respuesta de registro inesperada del servidor.");
                    return new AgentConfig
                    {
                        Server = new Uri(server).GetLeftPart(UriPartial.Authority),
                        Credential = credential,
                        MachineId = d.Int("machineId").Value,
                        AgentId = d.Int("agentId").Value,
                        CompanyPublicKey = d.Str("companyPublicKey"),
                    };
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new RejectedException();
            }
            catch (WebException ex) when (ex.Status == WebExceptionStatus.TrustFailure && Tls.LastProblem != null)
            {
                throw new InvalidOperationException($"No se confió en el servidor: {Tls.LastProblem}.");
            }
        }
    }

    /// <summary>Lo que identifica al equipo en el registro: nombre, MAC e IP de la tarjeta con puerta de enlace, sistema.</summary>
    public sealed class Identity
    {
        public string Hostname { get; set; }
        public string MacAddress { get; set; }
        public string IpAddress { get; set; }
        public string OperatingSystem { get; set; }
        public string MachineType { get; set; }

        public static Identity Current()
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .Select(n => new
                {
                    Adapter = n,
                    Props = n.GetIPProperties(),
                })
                .Select(x => new
                {
                    x.Adapter,
                    Ip = x.Props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.Address.ToString()).FirstOrDefault(),
                    Gateway = x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                        && !g.Address.Equals(IPAddress.Any)),
                })
                .Where(x => x.Ip != null)
                .OrderByDescending(x => x.Gateway)
                .ToList();
            var main = adapters.FirstOrDefault();
            var mac = main == null ? "" : string.Join(":", main.Adapter.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            var os = Wmi.OperatingSystem();
            return new Identity
            {
                Hostname = Environment.MachineName,
                MacAddress = mac,
                IpAddress = main?.Ip,
                OperatingSystem = os.Name,
                MachineType = os.IsServer ? "servidor" : Wmi.HasBattery() ? "laptop" : "computadora",
            };
        }
    }
}
