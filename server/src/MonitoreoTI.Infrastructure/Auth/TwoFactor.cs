using System.Collections.Concurrent;
using OtpNet;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>
/// TOTP (RFC 6238, 6 dígitos, 30 s) compatible con Google Authenticator, Microsoft Authenticator, etc.
/// Acepta un intervalo antes y uno después por desfase de reloj, y cada código sirve una sola vez.
/// </summary>
public sealed class TwoFactor(AuthOptions options, TimeProvider clock)
{
    // Último intervalo aceptado por usuario: impide reutilizar un código interceptado dentro de su ventana.
    private readonly ConcurrentDictionary<int, long> _lastUsedStep = new();

    public static string NewSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public string OtpAuthUri(string secret, string account) =>
        $"otpauth://totp/{Uri.EscapeDataString(options.TotpIssuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(options.TotpIssuer)}&digits=6&period=30";

    /// <summary>QR del URI otpauth como PNG en data URI, para mostrarlo en el panel sin librerías.</summary>
    public static string QrPngDataUri(string otpAuthUri) => Panel.QrImages.PngDataUri(otpAuthUri);

    public bool Verify(int userId, string secret, string code)
    {
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        if (!totp.VerifyTotp(clock.GetUtcNow().UtcDateTime, code, out var step, new VerificationWindow(previous: 1, future: 1)))
            return false;

        // Mismo intervalo o uno anterior al ya usado: es una repetición.
        var accepted = true;
        _lastUsedStep.AddOrUpdate(userId, step, (_, last) =>
        {
            if (step <= last)
            {
                accepted = false;
                return last;
            }
            return step;
        });
        return accepted;
    }
}
