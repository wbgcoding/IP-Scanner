namespace IpScanner.Core.Scanner;

/// <summary>Typo-tolerant match for the service-list search box: an
/// exact substring match first (fast path, also covers a plain port-number
/// search), then a small Levenshtein-distance fallback per word so "htpp"
/// still finds "HTTP" and "printr" still finds "Printer".</summary>
public static class ServiceSearch
{
    public static bool Matches(string name, int port, string query)
    {
        query = query.Trim();
        if (query.Length == 0) return true;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (port.ToString().Contains(query)) return true;

        int maxDistance = query.Length <= 4 ? 1 : query.Length <= 8 ? 2 : 3;
        foreach (var word in name.Split(' ', '-', '/'))
            if (word.Length > 0 && Levenshtein(word, query) <= maxDistance) return true;
        return false;
    }

    /// <summary>Classic edit distance (insert/delete/substitute), case-insensitive.
    /// Internal (not private) so ServiceSearchTests can assert on it directly.</summary>
    internal static int Levenshtein(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }
}
