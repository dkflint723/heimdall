namespace Heimdall.Core;

/// <summary>
/// Compares names the way a person reads them, so <c>file2</c> sorts before
/// <c>file10</c> rather than after it.
///
/// Ordinal comparison puts "10" before "2" because '1' &lt; '2', which is
/// correct for bytes and wrong for anything a person named. Digit runs are
/// compared as numbers, everything else ordinally.
///
/// Works on spans and never allocates: this runs once per comparison while
/// sorting a directory, and a 200,000-entry sort is millions of calls.
/// </summary>
public static class NaturalOrder
{
    public static int Compare(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int i = 0, j = 0;

        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                // Leading zeros do not change the value, but they do decide
                // ties: "01" and "1" are equal numerically, so the shorter run
                // wins to keep the order stable.
                var startA = i;
                var startB = j;

                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;

                var runA = a[startA..i].TrimStart('0');
                var runB = b[startB..j].TrimStart('0');

                if (runA.Length != runB.Length)
                    return runA.Length - runB.Length;

                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0) return digits;

                var padding = (i - startA) - (j - startB);
                if (padding != 0) return padding;

                continue;
            }

            var left = char.ToUpperInvariant(a[i]);
            var right = char.ToUpperInvariant(b[j]);

            if (left != right) return left - right;

            i++;
            j++;
        }

        return (a.Length - i) - (b.Length - j);
    }

    public static int Compare(string? a, string? b)
        => Compare(a.AsSpan(), b.AsSpan());
}
