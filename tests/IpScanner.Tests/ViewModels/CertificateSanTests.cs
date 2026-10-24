using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class CertificateSanTests
{
    [Fact]
    public void ParseSans_ListsDnsAndIpWithNeutralLabels()
    {
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=router", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("router.lan");
        san.AddIpAddress(IPAddress.Parse("192.168.2.1"));
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        Assert.Equal(new[] { "DNS: router.lan", "IP: 192.168.2.1" }, CertificateToolViewModel.ParseSans(cert));
    }

    [Fact]
    public void ParseSans_IsEmptyWithoutTheExtension()
    {
        using var key = ECDsa.Create();
        using var cert = new CertificateRequest("CN=bare", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Empty(CertificateToolViewModel.ParseSans(cert));
    }
}
