using System.Net;

namespace IpScanner.Core.Scanner;

public static class HostnameResolver
{
    public static string Normalize(string host)
        => host.EndsWith(".localdomain") ? host[..^".localdomain".Length] : host;

    public static string? Resolve(string ip)
    {
        try
        {
            var host = Dns.GetHostEntry(ip).HostName;
            return string.IsNullOrEmpty(host) ? null : Normalize(host);
        }
        catch { return null; }
    }
}
