using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace IpScanner.Core.Scanner;

/// <summary>Pure text-matching for the table's search box, kept apart from
/// the WPF window so the prefix parsing can be unit-tested directly.</summary>
public static class DeviceFilter
{
    /// <summary>Every space-separated term in <paramref name="filterText"/> must
    /// match (AND). A term may target one field via a leading "ip:", "mac:",
    /// "name:", "hersteller:"/"vendor:" or "port:" prefix (case-insensitive);
    /// without a recognized prefix it matches any of ip/hostname/mac/vendor, same
    /// as the old always-Contains behavior.</summary>
    public static bool Matches(string filterText, string ip, string hostname, string mac, string vendor,
        IEnumerable<int> ports)
    {
        if (filterText.Length == 0) return true;
        var portList = ports as ICollection<int> ?? ports.ToList();
        foreach (var term in filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!MatchesTerm(term, ip, hostname, mac, vendor, portList)) return false;
        return true;
    }

    private static bool MatchesTerm(string term, string ip, string hostname, string mac, string vendor,
        ICollection<int> ports)
    {
        var colon = term.IndexOf(':');
        if (colon > 0)
        {
            var value = term[(colon + 1)..];
            switch (term[..colon].ToLowerInvariant())
            {
                case "ip": return Has(ip, value);
                case "mac": return Has(mac, value);
                case "name": return Has(hostname, value);
                case "hersteller":
                case "vendor": return Has(vendor, value);
                case "port":
                    return ports.Any(p => Has(p.ToString(CultureInfo.InvariantCulture), value));
            }
        }
        return Has(ip, term) || Has(hostname, term) || Has(mac, term) || Has(vendor, term);
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>The three quick-filter pills, ANDed with each other (and,
    /// at the call site, with <see cref="Matches"/> for the text filter). Each
    /// pill only restricts when checked; an unchecked pill never excludes a row.</summary>
    public static bool PassesQuickFilters(bool onlyOnline, bool onlyPinned, bool onlyUnknown,
        bool isOnline, bool isPinned, bool isUnknown)
    {
        if (onlyOnline && !isOnline) return false;
        if (onlyPinned && !isPinned) return false;
        if (onlyUnknown && !isUnknown) return false;
        return true;
    }
}
