using System.Globalization;
using System.Numerics;

namespace DataGridScene.Core;

/// <summary>
/// Numeric comparison without rounding a binary float through decimal (or an integer through double).
/// Same-representation comparisons stay on native fast paths; mixed binary/decimal comparisons use
/// their exact rational values. Null ordering is handled by GridValue before reaching this class.
/// </summary>
internal static class GridNumber
{
    private static bool Binary(object value) => value is double or float;
    internal static int Compare(object a, object b)
    {
        bool left = Binary(a), right = Binary(b);
        if (left && right) return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
        if (!left && !right) return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
        double binary = Convert.ToDouble(left ? a : b, CultureInfo.InvariantCulture);
        int sign = left ? 1 : -1;
        // Match Double.CompareTo: all NaNs compare equal and precede non-NaN values.
        if (double.IsNaN(binary) || double.IsNegativeInfinity(binary)) return -sign;
        if (double.IsPositiveInfinity(binary)) return sign;
        decimal exact = Convert.ToDecimal(left ? b : a, CultureInfo.InvariantCulture);
        return sign * CompareFinite(binary, exact);
    }
    private static int CompareFinite(double binary, decimal exact)
    {
        var (bn, bd) = Fraction(binary);
        Span<int> bits = stackalloc int[4]; decimal.GetBits(exact, bits);
        BigInteger dn = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        if (bits[3] < 0) dn = -dn;
        BigInteger dd = BigInteger.Pow(10, (bits[3] >> 16) & 0xff);
        return (bn * dd).CompareTo(dn * bd);
    }
    private static (BigInteger Numerator, BigInteger Denominator) Fraction(double value)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7ff);
        ulong significand = bits & 0x000fffffffffffffUL;
        if (exponent != 0) significand |= 1UL << 52;
        int shift = exponent == 0 ? -1074 : exponent - 1075;
        BigInteger numerator = significand;
        if ((bits >> 63) != 0) numerator = -numerator;
        return shift >= 0 ? (numerator << shift, BigInteger.One) : (numerator, BigInteger.One << -shift);
    }
    internal static string Key(object value)
    {
        if (!Binary(value)) return "n:" + Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture);
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (number == 0) return "n:0"; // Collapse positive and negative zero across representations.
        // Retain existing decimal/integer keys only when the decimal value is exactly equal.
        // For other values, round-trip binary formatting keeps adjacent/subnormal floats distinct.
        if (double.IsFinite(number))
        {
            try
            {
                decimal candidate = Convert.ToDecimal(number, CultureInfo.InvariantCulture);
                if (CompareFinite(number, candidate) == 0) return "n:" + candidate.ToString("G29", CultureInfo.InvariantCulture);
            }
            catch (OverflowException) { }
        }
        return "n:binary:" + number.ToString("R", CultureInfo.InvariantCulture);
    }
}
