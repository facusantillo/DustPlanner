using System.Globalization;
using System.Text;

namespace DustPlanner.Cli.Rendering;

/// <summary>Minimal fixed-width table renderer (no dependencies, copy-paste friendly).</summary>
internal sealed class TextTable
{
    private readonly List<(string Header, bool RightAlign)> _columns = new();
    private readonly List<string[]> _rows = new();

    public TextTable AddColumn(string header, bool rightAlign = false)
    {
        _columns.Add((header, rightAlign));
        return this;
    }

    public TextTable AddRow(params object?[] cells)
    {
        _rows.Add(cells.Select(c => c switch
        {
            null => string.Empty,
            int i => i.ToString("N0", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => c.ToString() ?? string.Empty,
        }).ToArray());
        return this;
    }

    public string Render(string indent = "  ")
    {
        var widths = _columns.Select((c, i) => Math.Max(c.Header.Length, _rows.Count == 0 ? 0 : _rows.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        var sb = new StringBuilder();
        sb.Append(indent).AppendLine(Line(_columns.Select(c => c.Header).ToArray(), widths));
        sb.Append(indent).AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in _rows)
        {
            sb.Append(indent).AppendLine(Line(row, widths));
        }

        return sb.ToString();
    }

    private string Line(string[] cells, int[] widths)
    {
        var parts = new List<string>();
        for (var i = 0; i < widths.Length; i++)
        {
            var cell = i < cells.Length ? cells[i] : string.Empty;
            parts.Add(_columns[i].RightAlign ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i]));
        }

        return string.Join("  ", parts).TrimEnd();
    }
}
