using System.Net.Security;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

public class CertificateToolViewModelTests
{
    [Fact]
    public void DescribeChain_NoErrors_ReportsValid()
        => Assert.Equal(new[] { Loc.CertChainValid }, CertificateToolViewModel.DescribeChain(SslPolicyErrors.None));

    [Fact]
    public void DescribeChain_ChainErrors_ReportsUntrusted()
        => Assert.Equal(new[] { Loc.CertChainUntrusted },
            CertificateToolViewModel.DescribeChain(SslPolicyErrors.RemoteCertificateChainErrors));

    [Fact]
    public void DescribeChain_NameMismatch_ReportsNameMismatch()
        => Assert.Equal(new[] { Loc.CertChainNameMismatch },
            CertificateToolViewModel.DescribeChain(SslPolicyErrors.RemoteCertificateNameMismatch));

    [Fact]
    public void DescribeChain_NotAvailable_ReportsNoCertificate()
        => Assert.Equal(new[] { Loc.CertChainNoCertificate },
            CertificateToolViewModel.DescribeChain(SslPolicyErrors.RemoteCertificateNotAvailable));

    [Fact]
    public void DescribeChain_MultipleErrors_ReportsAllOfThem()
    {
        var errors = SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors;
        var findings = CertificateToolViewModel.DescribeChain(errors);
        Assert.Contains(Loc.CertChainNameMismatch, findings);
        Assert.Contains(Loc.CertChainUntrusted, findings);
        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void CanStart_FalseWhenTargetEmptyOrBusy()
    {
        var vm = new CertificateToolViewModel();
        Assert.False(vm.CanStart);
        vm.Target = "example.com";
        Assert.True(vm.CanStart);
        vm.IsBusy = true;
        Assert.False(vm.CanStart);
    }
}
