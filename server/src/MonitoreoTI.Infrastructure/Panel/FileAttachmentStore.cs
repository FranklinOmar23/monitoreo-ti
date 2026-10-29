using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Infrastructure.Panel;

public sealed class AttachmentOptions
{
    /// <summary>Carpeta raíz de los adjuntos (configuración "Adjuntos:Carpeta").</summary>
    public string Folder { get; set; } = "App_Data/adjuntos";
}

/// <summary>
/// Adjuntos en disco: {carpeta}/{empresa}/{ticket}/{guid}.{ext}. El nombre lo pone el servidor (nunca el cliente),
/// así que no hay rutas manipulables; al leer se comprueba igualmente que la ruta quede dentro de la carpeta.
/// </summary>
public sealed class FileAttachmentStore(AttachmentOptions options) : IAttachmentStore
{
    private string Root => Path.GetFullPath(options.Folder);

    public async Task<string> SaveAsync(int companyId, int ticketId, string extension, byte[] content, CancellationToken cancellationToken)
    {
        var relative = Path.Combine(companyId.ToString(), ticketId.ToString(), $"{Guid.NewGuid():N}.{extension}");
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, cancellationToken);
        return relative.Replace('\\', '/');
    }

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return null;
        return await File.ReadAllBytesAsync(full, cancellationToken);
    }
}
