using System.Globalization;

namespace InkShelf.Services;

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;
        var ix = 0;
        var iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var sx = ix;
                var sy = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;
                var nx = x[sx..ix].TrimStart('0');
                var ny = y[sy..iy].TrimStart('0');
                var length = nx.Length.CompareTo(ny.Length);
                if (length != 0) return length;
                var number = string.Compare(nx, ny, StringComparison.Ordinal);
                if (number != 0) return number;
            }
            else
            {
                var result = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (result != 0) return result;
                ix++;
                iy++;
            }
        }
        return x.Length.CompareTo(y.Length);
    }
}
