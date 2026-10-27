using System.Text;

namespace Transit.Gtfs;

/// <summary>
/// Minimal RFC 4180 reader for GTFS tables: header row, quoted fields, doubled quotes.
/// Embedded newlines inside quotes aren't supported; GTFS producers don't emit them in practice.
/// </summary>
public static class Csv
{
    public static IEnumerable<CsvRow> Read(TextReader reader)
    {
        var header = reader.ReadLine();
        if (header is null) yield break;
        var columns = Split(header.TrimStart('﻿'));
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Length; i++)
            index[columns[i].Trim()] = i;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length > 0)
                yield return new CsvRow(index, Split(line));
        }
    }

    public static string[] Split(string line)
    {
        if (!line.Contains('"'))
            return line.Split(',');

        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"')
                    field.Append(line[++i]);
                else
                    quoted = false;
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
                field.Append(c);
        }
        fields.Add(field.ToString());
        return [.. fields];
    }
}

public readonly record struct CsvRow(IReadOnlyDictionary<string, int> Index, string[] Fields)
{
    /// <summary>The named column's value, or "" when the column is absent from this table.</summary>
    public string this[string column] => Index.TryGetValue(column, out var i) && i < Fields.Length ? Fields[i] : "";
}
