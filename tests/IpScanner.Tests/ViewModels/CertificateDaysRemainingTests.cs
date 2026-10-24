using System;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IpScanner.Core.Localization;
using IpScanner.ViewModels.Tools;
using Xunit;

namespace IpScanner.Tests.ViewModels;

[Collection("Localization")]
public class CertificateDaysRemainingTests
{
    private static CertificateToolViewModel Apply(DateTime notAfter, SslPolicyErrors errors = SslPolicyErrors.None)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=test.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(notAfter.AddDays(-30), notAfter);
        var vm = new CertificateToolViewModel();
        typeof(CertificateToolViewModel).GetMethod("ApplyResult", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, new object[] { cert, errors });
        return vm;
    }

    [Fact]
    public void FarFuture_ShowsDaysRemaining_InPlainTextColour()
    {
        var vm = Apply(DateTime.Now.AddDays(100).AddHours(1));
        Assert.Equal(Loc.CertDaysRemainingMsg(100), vm.DaysRemainingText);
        Assert.Equal("Text", vm.DaysRemainingColorKey);
    }

    [Fact]
    public void UnderTwoWeeks_WarnsInPeach()
    {
        var vm = Apply(DateTime.Now.AddDays(5).AddHours(1));
        Assert.Equal(Loc.CertDaysRemainingMsg(5), vm.DaysRemainingText);
        Assert.Equal("Peach", vm.DaysRemainingColorKey);
    }

    [Fact]
    public void ExactlyFourteenDays_IsStillPlain()
    {
        var vm = Apply(DateTime.Now.AddDays(14).AddHours(1));
        Assert.Equal("Text", vm.DaysRemainingColorKey);
    }

    [Fact]
    public void Expired_ShowsExpiredMessage_InRed()
    {
        var vm = Apply(DateTime.Now.AddDays(-10).AddHours(-1));
        Assert.Equal(Loc.CertExpiredMsg(11), vm.DaysRemainingText);
        Assert.Equal("Red", vm.DaysRemainingColorKey);
    }

    [Fact]
    public void Result_ExposesSubjectKeySizeAndChainWord()
    {
        var vm = Apply(DateTime.Now.AddDays(60), SslPolicyErrors.RemoteCertificateNameMismatch);
        Assert.True(vm.HasResult);
        Assert.Contains("test.local", vm.Subject);
        Assert.Equal("2048", vm.KeySize);
        Assert.Contains(Loc.CertChainNameMismatch, vm.ChainFindings);
        Assert.Equal(64, vm.Fingerprint.Length);
    }
}
