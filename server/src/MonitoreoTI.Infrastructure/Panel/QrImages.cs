using QRCoder;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>Códigos QR como PNG (QRCoder), en data URI para mostrarlos o imprimirlos sin más librerías.</summary>
public static class QrImages
{
    /// <summary>Contenido de la etiqueta de un equipo: la app la reconoce y abre su ficha (sección 13).</summary>
    public static string MachineContent(Guid qrCode) => $"monitoreoti://q/{qrCode}";

    public static string PngDataUri(string content, int pixelsPerModule = 6)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
