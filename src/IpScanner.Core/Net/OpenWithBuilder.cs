using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IpScanner.Core.Net;

public enum OpenWithKind { BrowserHttp, BrowserHttps, RemoteDesktop, Ssh, Shares }

/// <summary>A ready-to-run launch. <see cref="FileName"/> is an absolute URL (browser kinds, started
/// through the shell) or an absolute path under the Windows folder; <see cref="Arguments"/> go to
/// <see cref="ProcessStartInfo.ArgumentList"/> one by one and are never joined into a command line.</summary>
public sealed record OpenWithCommand(OpenWithKind Kind, string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute)
{
    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo(FileName) { UseShellExecute = UseShellExecute };
        foreach (var a in Arguments) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>Builds the "Open with" launches for one device. The only thing that ever reaches a
/// process is an address that parsed as an IP, round-trips to its own canonical text and is
/// re-formatted by <see cref="IPAddress.ToString()"/>: host names, MAC strings and anything else
/// that came from the network are never accepted, so no argument can carry shell syntax,
/// an option, a path or a second URL. Nothing here starts a process; callers run
/// <see cref="OpenWithCommand.ToStartInfo"/>.</summary>
public static class OpenWithBuilder
{
    /// <summary>The TCP port an entry needs; a finished port scan that did not find it open disables the entry.</summary>
    public static int PortOf(OpenWithKind kind) => kind switch
    {
        OpenWithKind.BrowserHttp => 80,
        OpenWithKind.BrowserHttps => 443,
        OpenWithKind.RemoteDesktop => 3389,
        OpenWithKind.Ssh => 22,
        OpenWithKind.Shares => 445,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The launch for <paramref name="kind"/> on <paramref name="address"/>, or null when the
    /// address is not a plain unicast IP or the kind has no safe form for it. <paramref name="windowsDir"/>
    /// is the Windows folder the helper programs live in (injectable for tests).</summary>
    public static OpenWithCommand? Build(OpenWithKind kind, string? address, string? windowsDir = null)
    {
        if (!TryCanonical(address, out var ip, out var text)) return null;
        windowsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windowsDir)) return null;

        switch (kind)
        {
            case OpenWithKind.BrowserHttp:
                return Url(kind, Uri.UriSchemeHttp, ip);
            case OpenWithKind.BrowserHttps:
                return Url(kind, Uri.UriSchemeHttps, ip);
            case OpenWithKind.RemoteDesktop:
                return new OpenWithCommand(kind, Path.Combine(windowsDir, "System32", "mstsc.exe"),
                    new[] { "/v:" + text }, false);
            case OpenWithKind.Ssh:
                return new OpenWithCommand(kind, Path.Combine(windowsDir, "System32", "OpenSSH", "ssh.exe"),
                    new[] { text }, false);
            case OpenWithKind.Shares:
                return UncShare(ip, text) is { } unc
                    ? new OpenWithCommand(kind, Path.Combine(windowsDir, "explorer.exe"), new[] { unc }, false)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Accepts an address only when it is a unicast IPv4/IPv6 whose own canonical text equals the input
    /// (so "127.1", "0x7f.1", "010.0.0.1", a bare number, whitespace, ports, hosts and suffixes all fail), then
    /// hands back the re-formatted text.</summary>
    internal static bool TryCanonical(string? address, out IPAddress ip, out string text)
    {
        ip = IPAddress.None;
        text = "";
        if (string.IsNullOrEmpty(address) || address.Length > 64) return false;
        if (!IPAddress.TryParse(address, out var parsed)) return false;
        if (parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) return false;
        var canonical = parsed.ToString();
        if (!string.Equals(canonical, address, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsUsableTarget(parsed)) return false;
        ip = parsed;
        text = canonical;
        return true;
    }

    // Not a host one can connect to: unspecified, broadcast, multicast.
    private static bool IsUsableTarget(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.Broadcast) && b[0] < 224;
        }
        return !ip.Equals(IPAddress.IPv6Any) && !ip.IsIPv6Multicast;
    }

    private static OpenWithCommand? Url(OpenWithKind kind, string scheme, IPAddress ip)
    {
        // The host goes through UriBuilder (brackets for IPv6, scope id percent-encoded); the result is
        // the only string started through the shell.
        var host = ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0
            ? ip.ToString().Replace("%", "%25")
            : ip.ToString();
        try
        {
            var uri = new UriBuilder(scheme, host).Uri;
            return uri.Scheme == scheme ? new OpenWithCommand(kind, uri.AbsoluteUri, Array.Empty<string>(), true) : null;
        }
        catch (UriFormatException) { return null; }
    }

    // \\192.168.1.5 for IPv4; IPv6 uses the ipv6-literal.net form (':' -> '-', '%' -> 's'). An IPv4-mapped
    // address has a '.' in its tail that the literal form cannot carry, so it gets no share entry.
    private static string? UncShare(IPAddress ip, string text)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork) return @"\\" + text;
        if (ip.IsIPv4MappedToIPv6 || text.Contains('.')) return null;
        return @"\\" + text.Replace(':', '-').Replace('%', 's') + ".ipv6-literal.net";
    }
}
