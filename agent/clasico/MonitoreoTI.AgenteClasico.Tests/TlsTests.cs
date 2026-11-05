using System;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MonitoreoTI.AgenteClasico;
using Xunit;

namespace MonitoreoTI.AgenteClasico.Tests
{
    /// <summary>
    /// Certificados como los del servidor de desarrollo (ECDSA, CA propia, IP en los nombres alternativos). Windows 7 los
    /// reporta con "nombre que no coincide" y "cadena no confiable": el agente debe aceptarlos solo si la IP está en el
    /// certificado y la cadena termina en la CA de Centinela.
    /// </summary>
    public class TlsTests
    {
        private static (X509Certificate2 Ca, X509Certificate2 Server) Pair(string ip = "192.168.3.115")
        {
            var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest("CN=MonitoreoTI Desarrollo CA", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=MonitoreoTI servidor de desarrollo", serverKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Parse(ip));
            san.AddIpAddress(IPAddress.Loopback);
            san.AddDnsName("localhost");
            san.AddDnsName("PC-042076");
            request.CertificateExtensions.Add(san.Build());
            var server = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 1, 2, 3, 4 });
            return (new X509Certificate2(ca.Export(X509ContentType.Cert)), server);
        }

        private const SslPolicyErrors Windows7 = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;

        [Fact]
        public void Accepts_the_server_by_ip_when_the_chain_ends_in_the_centinela_ca()
        {
            var (ca, server) = Pair();
            Tls.UseTrustedRoot(ca);
            Tls.ExpectedHost = "192.168.3.115";
            Assert.True(Tls.Validate(null, server, null, Windows7), Tls.LastProblem);
            Tls.ExpectedHost = "PC-042076";
            Assert.True(Tls.Validate(null, server, null, Windows7), Tls.LastProblem);
        }

        [Fact]
        public void Rejects_another_host_and_says_why()
        {
            var (ca, server) = Pair();
            Tls.UseTrustedRoot(ca);
            Tls.ExpectedHost = "192.168.3.200";
            Assert.False(Tls.Validate(null, server, null, Windows7));
            Assert.Contains("no incluye 192.168.3.200", Tls.LastProblem);
            Assert.Contains("192.168.3.115", Tls.LastProblem);
        }

        [Fact]
        public void Rejects_a_certificate_from_another_ca_or_without_the_ca_file()
        {
            var (_, server) = Pair();
            var (otherCa, _) = Pair();
            Tls.UseTrustedRoot(otherCa);
            Tls.ExpectedHost = "192.168.3.115";
            Assert.False(Tls.Validate(null, server, null, Windows7));
            Assert.NotNull(Tls.LastProblem);

            Tls.UseTrustedRoot(null);
            Assert.False(Tls.Validate(null, server, null, Windows7));
            Assert.Contains("falta el certificado de Centinela", Tls.LastProblem);
        }

        [Fact]
        public void A_certificate_windows_already_trusts_passes()
        {
            var (_, server) = Pair();
            Assert.True(Tls.Validate(null, server, null, SslPolicyErrors.None));
        }
    }
}
