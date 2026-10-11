using System.Globalization;
using System.Text;

namespace DataGridScene.Core;

/// <summary>Bound formatting work before user-supplied layout formats reach numeric formatting.</summary>
internal static class SummaryFormat
{
    internal static void Validate(string format)
    {
        if (format.Length > 4096 || CompositeFormat.Parse(format).MinimumArgumentCount > 3)
            throw new FormatException("Summary formats accept {0}=value, {1}=caption and {2}=kind, with at most 4,096 characters.");
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '{') continue;
            if (i + 1 < format.Length && format[i + 1] == '{') { i++; continue; }
            // CompositeFormat has already checked brace syntax and argument indexes.
            int end = format.IndexOf('}', i + 1);
            var body = format.AsSpan(i + 1, end - i - 1);
            int colon = body.IndexOf(':');
            var header = colon < 0 ? body : body[..colon];
            int comma = header.IndexOf(',');
            if (comma >= 0 && (!int.TryParse(header[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int alignment) ||
                alignment < -4096 || alignment > 4096))
                throw new FormatException("Summary format alignment exceeds 4,096 characters.");
            if (colon >= 0)
            {
                var specifier = body[(colon + 1)..];
                bool numericPrecision = specifier.Length > 1 && char.IsAsciiLetter(specifier[0]);
                for (int j = 1; numericPrecision && j < specifier.Length; j++) numericPrecision = char.IsAsciiDigit(specifier[j]);
                if (numericPrecision && (!int.TryParse(specifier[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int precision) || precision > 1024))
                    throw new FormatException("Summary numeric precision exceeds 1,024 digits.");
            }
            i = end;
        }
    }
}
