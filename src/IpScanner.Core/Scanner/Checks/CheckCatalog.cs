namespace IpScanner.Core.Scanner.Checks;

/// <summary>The built-in checks, in run order. There is no scripting and nothing downloadable:
/// every check here is code reviewed and shipped with the app.</summary>
internal static class CheckCatalog
{
    public static IReadOnlyList<ICheck> Create(ICheckIo io) => new ICheck[]
    {
        new HttpTitleCheck(io),
        new HttpSecurityHeadersCheck(io),
        new TlsCertificateCheck(io),
        new SnmpSysDescrCheck(io),
        new UpnpDeviceCheck(io),
        new MdnsServicesCheck(io),
        new NetBiosNamesCheck(io),
        new DhcpDiscoveryCheck(io),
    };
}
