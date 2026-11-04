using System.Globalization;
using System.Text;

namespace MonitoreoTI.Application.Network;

/// <summary>Una fila del archivo con su número de línea (para señalar errores) y sus valores por columna.</summary>
public sealed record CsvRow(int Line, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>El valor sin espacios, o null si la columna no existe o está vacía.</summary>
    public string? Get(string column) => Values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// Lector de CSV (RFC 4180): comillas, comillas dobles escapadas y saltos de línea dentro de comillas. El separador
/// se deduce de la primera línea (coma, punto y coma —el que usa Excel en español— o tabulador). Los encabezados se
/// comparan sin mayúsculas, tildes ni espacios: "Posición U" es "posicion_u".
/// </summary>
public sealed class CsvTable
{
    private CsvTable(IReadOnlyList<string> columns, IReadOnlyList<CsvRow> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<string> Columns { get; }
    public IReadOnlyList<CsvRow> Rows { get; }

    public static CsvTable Parse(string text)
    {
        if (text.Length > 0 && text[0] == (char)0xFEFF)
            text = text[1..];
        var records = Split(text, Separator(text));
        if (records.Count == 0)
            return new CsvTable([], []);

        var columns = records[0].Fields.Select(NormalizeHeader).ToList();
        var rows = records.Skip(1)
            .Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            .Select(r =>
            {
                var values = new Dictionary<string, string>();
                for (var i = 0; i < columns.Count && i < r.Fields.Count; i++)
                    if (columns[i].Length > 0)
                        values.TryAdd(columns[i], r.Fields[i]);
                return new CsvRow(r.Line, values);
            })
            .ToList();
        return new CsvTable(columns, rows);
    }

    public static string NormalizeHeader(string header)
    {
        var decomposed = header.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var text = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            text.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return string.Join('_', text.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static char Separator(string text)
    {
        var end = text.IndexOf('\n');
        var header = end < 0 ? text : text[..end];
        return new[] { ';', '\t', ',' }.OrderByDescending(c => header.Count(x => x == c)).First();
    }

    private static List<(int Line, List<string> Fields)> Split(string text, char separator)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                {
                    if (c == '\n')
                        line++;
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0)
                quoted = true;
            else if (c == separator)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                fields.Add(field.ToString());
                field.Clear();
                records.Add((recordLine, fields));
                fields = [];
                line++;
                recordLine = line;
            }
            else
                field.Append(c);
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((recordLine, fields));
        }
        return records;
    }
}
