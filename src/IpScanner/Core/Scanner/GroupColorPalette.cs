namespace IpScanner.Core.Scanner;

/// <summary>
/// Distinct group colors as #RRGGBB. Ports the Python max-diversity + min-distance
/// filtering over the xterm-256 dynamic palette into precomputed RGB hex values.
/// </summary>
public static class GroupColorPalette
{
    private static readonly int[] XtermDynamic =
    {
        196,202,208,214,220,226,190,154,118,82,
        46,51,21,27,33,39,45,50,63,69,
        75,81,87,93,99,105,111,117,129,135,
        141,147,201,207,213,219,225,231,165,171
    };
    private const int MinDistSq = 16900;

    public static IReadOnlyList<string> Sequence { get; } = Build();

    public static string ColorForIndex(int i) => Sequence[i % Sequence.Count];

    private static List<string> Build()
    {
        var filtered = FilterDiverse(XtermDynamic.ToList(), MinDistSq);
        var ordered = MaxDiversity(filtered);
        return ordered.Select(n =>
        {
            var (r, g, b) = XtermToRgb(n);
            return $"#{r:X2}{g:X2}{b:X2}";
        }).ToList();
    }

    private static (int r, int g, int b) XtermToRgb(int n)
    {
        int[][] basic16 =
        {
            new[]{0,0,0}, new[]{128,0,0}, new[]{0,128,0}, new[]{128,128,0},
            new[]{0,0,128}, new[]{128,0,128}, new[]{0,128,128}, new[]{192,192,192},
            new[]{128,128,128}, new[]{255,0,0}, new[]{0,255,0}, new[]{255,255,0},
            new[]{0,0,255}, new[]{255,0,255}, new[]{0,255,255}, new[]{255,255,255}
        };
        if (n < 16) return (basic16[n][0], basic16[n][1], basic16[n][2]);
        if (n < 232)
        {
            n -= 16;
            int Conv(int x) => x == 0 ? 0 : 55 + 40 * x;
            return (Conv(n / 36), Conv(n % 36 / 6), Conv(n % 6));
        }
        int v = 8 + (n - 232) * 10;
        return (v, v, v);
    }

    private static int DistSq(int a, int b)
    {
        var (ra, ga, ba) = XtermToRgb(a);
        var (rb, gb, bb) = XtermToRgb(b);
        return (ra - rb) * (ra - rb) + (ga - gb) * (ga - gb) + (ba - bb) * (ba - bb);
    }

    private static List<int> FilterDiverse(List<int> colors, int minDistSq)
    {
        var pool = new List<int>(colors);
        bool changed = true;
        while (changed)
        {
            changed = false;
            var close = pool.ToDictionary(c => c,
                c => pool.Count(o => o != c && DistSq(c, o) < minDistSq));
            int worstScore = close.Values.Max();
            if (worstScore > 0)
            {
                int worst = pool.Where(c => close[c] == worstScore).OrderBy(c => -c).First();
                pool.Remove(worst);
                changed = true;
            }
        }
        return pool;
    }

    private static List<int> MaxDiversity(List<int> colors)
    {
        if (colors.Count <= 1) return new List<int>(colors);
        var remaining = new List<int>(colors);
        int first = remaining.OrderByDescending(c => XtermToRgb(c).r).First();
        var ordered = new List<int> { first };
        remaining.Remove(first);
        while (remaining.Count > 0)
        {
            int best = remaining.OrderByDescending(c => ordered.Min(p => DistSq(c, p))).First();
            ordered.Add(best);
            remaining.Remove(best);
        }
        return ordered;
    }
}
