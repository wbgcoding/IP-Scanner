namespace IpScanner.Core.Models;

public sealed class NetworkInfo
{
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public string? Gateway { get; set; }
    public string? SubnetMask { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public string Interface { get; set; } = "";

    /// <summary>"192.168.1.0/24" style label, or null.</summary>
    public string? Cidr =>
        Ip is null ? null : $"{string.Join('.', Ip.Split('.')[..3])}.0/24";
}
