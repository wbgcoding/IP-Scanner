using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core;
using Xunit;

namespace IpScanner.Tests.Core;

/// <summary>Signature verification, release pairing, redirect handling and the install gate of the
/// update flow. Every key pair here is generated inside the tests; no key exists on disk.</summary>
public class UpdateSignatureTests
{
    private const string ExeUrl = "https://github.com/wbgcoding/IP-Scanner/releases/download/v999.0.0/IP-Scanner.exe";
    private const string ShaUrl = ExeUrl + ".sha256";
    private const string SigUrl = ExeUrl + ".sig";
    private const string PageUrl = "https://github.com/wbgcoding/IP-Scanner/releases/tag/v999.0.0";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requested) Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>A body that delivers a few bytes and then never answers a read again.</summary>
    private sealed class StallingStream : Stream
    {
        private bool _gaveBytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_gaveBytes) { _gaveBytes = true; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Ok(string body) => Ok(Encoding.UTF8.GetBytes(body));

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static (ECDsa Key, string Spki) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static byte[] Exe(string marker = "EXE") => Encoding.UTF8.GetBytes("MZ-fake-exe-" + marker + new string('x', 5000));

    private static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static UpdateService.Release SignedRelease() =>
        new(new Version(999, 0, 0), ExeUrl, ShaUrl, SigUrl, PageUrl);

    private sealed record Launch(string RunDir, string NewExe, string Hash);

    /// <summary>Runs the install flow against a fake server that serves <paramref name="exe"/> with the
    /// given checksum text and signature; returns the result and the launcher call, if any.</summary>
    private static async Task<(UpdateService.ApplyResult Result, Launch? Launched, FakeHandler Handler)> Install(
        byte[] exe, string shaText, byte[] signature, string spki,
        Func<HttpRequestMessage, HttpResponseMessage?>? overrideRespond = null,
        Version? embeddedVersion = null, Version? installed = null)
    {
        var handler = new FakeHandler(req =>
        {
            if (overrideRespond?.Invoke(req) is { } custom) return custom;
            return req.RequestUri!.ToString() switch
            {
                ExeUrl => Ok(exe),
                ShaUrl => Ok(shaText),
                SigUrl => Ok(Convert.ToBase64String(signature)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        Launch? launched = null;
        var result = await UpdateService.DownloadAndApplyAsync(SignedRelease(), handler, spki,
            (runDir, newExe, hash, _, _) => launched = new Launch(runDir, newExe, hash),
            readFileVersion: _ => embeddedVersion ?? new Version(999, 0, 0), installedVersion: installed ?? new Version(2, 10, 0));
        return (result, launched, handler);
    }

    private static void Cleanup(Launch? l)
    {
        if (l is null) return;
        try { Directory.Delete(l.RunDir, recursive: true); } catch { }
    }

    // ── VerifySignature ──

    [Fact]
    public void VerifySignature_AcceptsValidSignature()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        using var stream = new MemoryStream(exe);
        Assert.True(UpdateService.VerifySignature(stream, Sign(key, exe), spki));
    }

    [Fact]
    public void VerifyDigestSignature_MatchesTheStreamVariant_AndRejectsAWrongDigest()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var sig = Sign(key, exe);
        Assert.True(UpdateService.VerifyDigestSignature(SHA256.HashData(exe), sig, spki));
        Assert.False(UpdateService.VerifyDigestSignature(SHA256.HashData(Exe("OTHER")), sig, spki));
        Assert.False(UpdateService.VerifyDigestSignature(new byte[16], sig, spki));   // not a SHA-256 digest
    }

    [Fact]
    public void VerifySignature_RejectsFlippedExeByte()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var sig = Sign(key, exe);
        exe[100] ^= 0x01;
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, sig, spki));
    }

    [Fact]
    public void VerifySignature_RejectsFlippedSignatureByte()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var sig = Sign(key, exe);
        sig[5] ^= 0x01;
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, sig, spki));
    }

    [Fact]
    public void VerifySignature_RejectsSignatureFromAnotherKey()
    {
        var (other, _) = NewKey();
        var (_, spki) = NewKey();
        var exe = Exe();
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, Sign(other, exe), spki));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void VerifySignature_RejectsUnusableKey(string spki)
    {
        var (key, _) = NewKey();
        var exe = Exe();
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, Sign(key, exe), spki));
    }

    [Fact]
    public void VerifySignature_PlaceholderConstantRefusesEverything()
    {
        var (key, _) = NewKey();
        var exe = Exe();
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, Sign(key, exe), UpdateService.PlaceholderPublicKey));
    }

    [Fact]
    public void VerifySignature_EmptySignatureIsRefused()
    {
        var (_, spki) = NewKey();
        using var stream = new MemoryStream(Exe());
        Assert.False(UpdateService.VerifySignature(stream, [], spki));
    }

    [Fact]
    public void VerifySignature_RejectsNonP256Key()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var exe = Exe();
        var sig = p384.SignData(exe, HashAlgorithmName.SHA256);
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, sig, Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void ShippedPublicKey_IsAValidP256Key_NotThePlaceholder()
    {
        var shipped = UpdateService.UpdatePublicKey;
        Assert.NotEqual(UpdateService.PlaceholderPublicKey, shipped);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(shipped), out _);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public void ShippedPublicKey_VerifiesASignatureProducedByTheSigningTool()
    {
        // Fixture signed once with the real tool: guards the key constant and the signature format together.
        const string sig = "4QNJIZaN6bW9rBZkgUljKXzkWR+p8BiPRc9vdwGmT7RCqQnU8fzMBychhAteeGENYMVHr99iqveDsxMiIhiuwA==";
        using var ok = new MemoryStream(Encoding.ASCII.GetBytes("signature-format-fixture"));
        Assert.True(UpdateService.VerifySignature(ok, Convert.FromBase64String(sig), UpdateService.UpdatePublicKey));
        using var bad = new MemoryStream(Encoding.ASCII.GetBytes("signature-format-fixturf"));
        Assert.False(UpdateService.VerifySignature(bad, Convert.FromBase64String(sig), UpdateService.UpdatePublicKey));
    }

    [Fact]
    public void ShippedPublicKey_RejectsSignatureFromTestKey()
    {
        var (key, _) = NewKey();
        var exe = Exe();
        using var stream = new MemoryStream(exe);
        Assert.False(UpdateService.VerifySignature(stream, Sign(key, exe), UpdateService.UpdatePublicKey));
    }

    // ── CheckAsync pairing ──

    private static string ReleaseJson(string assetsJson, string? htmlUrl = PageUrl) =>
        "{\"tag_name\":\"v999.0.0\"," + (htmlUrl is null ? "" : $"\"html_url\":\"{htmlUrl}\",") + $"\"assets\":[{assetsJson}]}}";

    private static string Asset(string name, string url) => $"{{\"name\":\"{name}\",\"browser_download_url\":\"{url}\"}}";

    private static Task<UpdateService.CheckResult> Check(string json) =>
        UpdateService.CheckAsync(new FakeHandler(_ => Ok(json)));

    [Fact]
    public async Task CheckAsync_ReleaseWithoutSig_IsUpdateAvailableUnsigned()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)));
        Assert.Equal(UpdateService.CheckStatus.UpdateAvailableUnsigned, r.Status);
        Assert.Null(r.Release!.SigUrl);
        Assert.Equal(PageUrl, r.Release.PageUrl);
    }

    [Fact]
    public async Task CheckAsync_ReleaseWithSig_IsUpdateAvailable()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)
            + "," + Asset("IP-Scanner.exe.sig", SigUrl)));
        Assert.Equal(UpdateService.CheckStatus.UpdateAvailable, r.Status);
        Assert.Equal(SigUrl, r.Release!.SigUrl);
    }

    [Fact]
    public async Task CheckAsync_SigFromForeignHost_DoesNotCountAsSigned()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)
            + "," + Asset("IP-Scanner.exe.sig", "https://evil.example/IP-Scanner.exe.sig")));
        Assert.Equal(UpdateService.CheckStatus.UpdateAvailableUnsigned, r.Status);
    }

    [Fact]
    public async Task CheckAsync_SigForADifferentExe_DoesNotCountAsSigned()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)
            + "," + Asset("Other.exe.sig", "https://github.com/x/Other.exe.sig")));
        Assert.Equal(UpdateService.CheckStatus.UpdateAvailableUnsigned, r.Status);
    }

    [Fact]
    public async Task CheckAsync_ConsoleExeWithChecksum_IsNeverPickedForTheUpdate()
    {
        var cli = "https://github.com/wbgcoding/IP-Scanner/releases/download/v999.0.0/ipscan-cli.exe";
        var r = await Check(ReleaseJson(Asset("ipscan-cli.exe", cli) + "," + Asset("ipscan-cli.exe.sha256", cli + ".sha256")
            + "," + Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)));
        Assert.Equal(ExeUrl, r.Release!.DownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_OnlyAConsoleExeInTheRelease_Fails()
    {
        var cli = "https://github.com/wbgcoding/IP-Scanner/releases/download/v999.0.0/ipscan-cli.exe";
        var r = await Check(ReleaseJson(Asset("ipscan-cli.exe", cli) + "," + Asset("ipscan-cli.exe.sha256", cli + ".sha256")));
        Assert.Equal(UpdateService.CheckStatus.Failed, r.Status);
    }

    [Fact]
    public async Task CheckAsync_ReleaseWithoutChecksum_Fails()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sig", SigUrl)));
        Assert.Equal(UpdateService.CheckStatus.Failed, r.Status);
    }

    [Fact]
    public async Task CheckAsync_UntrustedPageUrl_FallsBackToReleasesPage()
    {
        var r = await Check(ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl),
            "https://evil.example/page"));
        Assert.Equal("https://github.com/wbgcoding/IP-Scanner/releases/latest", r.Release!.PageUrl);
    }

    [Fact]
    public async Task CheckAsync_RedirectWithinTheApi_IsFollowed()
    {
        var json = ReleaseJson(Asset("IP-Scanner.exe", ExeUrl) + "," + Asset("IP-Scanner.exe.sha256", ShaUrl)
            + "," + Asset("IP-Scanner.exe.sig", SigUrl));
        const string moved = "https://api.github.com/repositories/123/releases/latest";
        var handler = new FakeHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/releases/latest") && req.RequestUri.Host == "api.github.com"
            && !req.RequestUri.AbsolutePath.StartsWith("/repositories") ? Redirect(moved) : Ok(json));
        var r = await UpdateService.CheckAsync(handler);
        Assert.Equal(UpdateService.CheckStatus.UpdateAvailable, r.Status);
        Assert.Contains(moved, handler.Requested);
    }

    [Theory]
    [InlineData("https://evil.example/releases/latest")]
    [InlineData("http://api.github.com/repositories/123/releases/latest")]
    [InlineData("https://objects.githubusercontent.com/x")]
    public async Task CheckAsync_RedirectOutsideTheApi_FailsWithoutRequestingTheTarget(string target)
    {
        var handler = new FakeHandler(_ => Redirect(target));
        var r = await UpdateService.CheckAsync(handler);
        Assert.Equal(UpdateService.CheckStatus.Failed, r.Status);
        Assert.DoesNotContain(target, handler.Requested);
    }

    // ── Redirect target rule ──

    [Theory]
    [InlineData("https://github.com/a/b", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("https://githubusercontent.com/x", false)]
    [InlineData("https://objects.githubusercontent.com.evil.example/x", false)]
    [InlineData("https://objects.githubusercontent.com@evil.example/x", false)]
    [InlineData("https://user:pw@github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    public void IsAllowedRedirectTarget_OnlyHttpsOnGithubHosts(string url, bool expected)
        => Assert.Equal(expected, UpdateService.IsAllowedRedirectTarget(new Uri(url)));

    // ── Install flow ──

    [Fact]
    public async Task Install_ValidSignatureAndChecksum_Launches_WithVerifiedFile()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, Hex(exe), Sign(key, exe), spki);
        try
        {
            Assert.True(result.Success);
            Assert.NotNull(launched);
            Assert.Equal(Hex(exe), launched!.Hash);
            Assert.Equal(exe, File.ReadAllBytes(launched.NewExe));
        }
        finally { Cleanup(launched); }
    }

    [Fact]
    public async Task Install_TamperedExe_IsRefusedWithSignatureError_AndNotLaunched()
    {
        var (key, spki) = NewKey();
        var good = Exe();
        var evil = Exe("EVIL");
        // Checksum file matches the tampered bytes (a publisher-controlled pair), the signature is for the good ones.
        var (result, launched, _) = await Install(evil, Hex(evil), Sign(key, good), spki);
        Assert.False(result.Success);
        Assert.IsType<UpdateSignatureException>(result.Error);
        Assert.Null(launched);
    }

    [Theory]
    [InlineData("998.0.0")]    // signed older build uploaded under a newer tag
    [InlineData("999.0.1")]
    [InlineData("2.10.0")]     // the installed version itself
    [InlineData("0.0.0")]      // no version resource
    public async Task Install_EmbeddedVersionNotTheAnnouncedOne_IsRefused_AndNotLaunched(string embedded)
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, Hex(exe), Sign(key, exe), spki, embeddedVersion: Version.Parse(embedded));
        Assert.False(result.Success);
        Assert.IsType<UpdateSignatureException>(result.Error);
        Assert.Null(launched);
    }

    [Fact]
    public async Task Install_AnnouncedVersionNotNewerThanInstalled_IsRefused()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, Hex(exe), Sign(key, exe), spki, installed: new Version(999, 0, 0));
        Assert.False(result.Success);
        Assert.Null(launched);
    }

    [Fact]
    public async Task Install_UnreadableEmbeddedVersion_IsRefused()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var called = false;
        var result = await UpdateService.DownloadAndApplyAsync(SignedRelease(), ServeAll(exe, key), spki,
            (_, _, _, _, _) => called = true, readFileVersion: _ => null, installedVersion: new Version(2, 10, 0));
        Assert.False(result.Success);
        Assert.False(called);
    }

    [Fact]
    public async Task Install_RealFileVersionReader_RefusesAFileWithoutVersionInfo()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var called = false;
        var result = await UpdateService.DownloadAndApplyAsync(SignedRelease(), ServeAll(exe, key), spki, (_, _, _, _, _) => called = true);
        Assert.False(result.Success);
        Assert.False(called);
    }

    private static FakeHandler ServeAll(byte[] exe, ECDsa key) => new(req => req.RequestUri!.ToString() switch
    {
        ExeUrl => Ok(exe),
        ShaUrl => Ok(Hex(exe)),
        SigUrl => Ok(Convert.ToBase64String(Sign(key, exe))),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    [Fact]
    public async Task Install_PlaceholderKey_RefusesEvenAValidPair()
    {
        var (key, _) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, Hex(exe), Sign(key, exe), UpdateService.PlaceholderPublicKey);
        Assert.False(result.Success);
        Assert.IsType<UpdateSignatureException>(result.Error);
        Assert.Null(launched);
    }

    [Fact]
    public async Task Install_ChecksumMismatch_IsRefusedWithoutSignatureError()
    {
        // The signature is valid; only the checksum gate fails, so the reason differs from a signature failure.
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, new string('A', 64), Sign(key, exe), spki);
        Assert.False(result.Success);
        Assert.Null(result.Error);
        Assert.Null(launched);
    }

    [Fact]
    public async Task Install_MalformedSignatureAsset_IsRefusedWithSignatureError()
    {
        var (_, spki) = NewKey();
        var exe = Exe();
        var handler = new FakeHandler(req => req.RequestUri!.ToString() switch
        {
            ExeUrl => Ok(exe),
            ShaUrl => Ok(Hex(exe)),
            SigUrl => Ok("%%% not base64 %%%"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var called = false;
        var result = await UpdateService.DownloadAndApplyAsync(SignedRelease(), handler, spki, (_, _, _, _, _) => called = true,
            readFileVersion: _ => new Version(999, 0, 0), installedVersion: new Version(2, 10, 0));
        Assert.False(result.Success);
        Assert.IsType<UpdateSignatureException>(result.Error);
        Assert.False(called);
    }

    [Fact]
    public async Task Install_UnsignedRelease_IsRefusedWithoutAnyRequest()
    {
        var handler = new FakeHandler(_ => Ok("x"));
        var unsigned = SignedRelease() with { SigUrl = null };
        var result = await UpdateService.DownloadAndApplyAsync(unsigned, handler, "x", (_, _, _, _, _) => { });
        Assert.False(result.Success);
        Assert.IsType<UpdateSignatureException>(result.Error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task Install_ForeignAssetUrl_IsRefusedWithoutAnyRequest()
    {
        var handler = new FakeHandler(_ => Ok("x"));
        var rel = SignedRelease() with { DownloadUrl = "https://evil.example/IP-Scanner.exe" };
        var result = await UpdateService.DownloadAndApplyAsync(rel, handler, "x", (_, _, _, _, _) => { });
        Assert.False(result.Success);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task Install_RedirectToAllowedCdnHost_IsFollowed()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        const string cdn = "https://objects.githubusercontent.com/blob/abc";
        var (result, launched, handler) = await Install(exe, Hex(exe), Sign(key, exe), spki,
            req => req.RequestUri!.ToString() == ExeUrl ? Redirect(cdn)
                 : req.RequestUri.ToString() == cdn ? Ok(exe) : null);
        try
        {
            Assert.True(result.Success);
            Assert.Contains(cdn, handler.Requested);
        }
        finally { Cleanup(launched); }
    }

    [Theory]
    [InlineData("https://evil.example/IP-Scanner.exe")]
    [InlineData("http://objects.githubusercontent.com/blob/abc")]
    [InlineData("https://github.com.evil.example/IP-Scanner.exe")]
    [InlineData("https://objects.githubusercontent.com@evil.example/x")]
    public async Task Install_RedirectToDisallowedTarget_IsRefused_AndTargetNeverRequested(string target)
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, handler) = await Install(exe, Hex(exe), Sign(key, exe), spki,
            req => req.RequestUri!.ToString() == ExeUrl ? Redirect(target) : null);
        Assert.False(result.Success);
        Assert.Null(launched);
        Assert.DoesNotContain(handler.Requested, r => r.Contains("evil.example", StringComparison.OrdinalIgnoreCase)
                                                  || r.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Install_RedirectToForeignHost_OnTheChecksumAsset_IsRefusedToo()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, handler) = await Install(exe, Hex(exe), Sign(key, exe), spki,
            req => req.RequestUri!.ToString() == ShaUrl ? Redirect("https://evil.example/sha") : null);
        Assert.False(result.Success);
        Assert.Null(launched);
        Assert.DoesNotContain(handler.Requested, r => r.Contains("evil.example"));
    }

    [Fact]
    public async Task Install_MoreThanFiveRedirects_IsRefused()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, handler) = await Install(exe, Hex(exe), Sign(key, exe), spki, req =>
        {
            var u = req.RequestUri!.ToString();
            if (u == ExeUrl) return Redirect("https://objects.githubusercontent.com/loop/1");
            if (u.StartsWith("https://objects.githubusercontent.com/loop/", StringComparison.Ordinal))
                return Redirect("https://objects.githubusercontent.com/loop/" + (int.Parse(u[(u.LastIndexOf('/') + 1)..]) + 1));
            return null;
        });
        Assert.False(result.Success);
        Assert.Null(launched);
        Assert.DoesNotContain("https://objects.githubusercontent.com/loop/7", handler.Requested);   // stopped at the cap
    }

    [Fact]
    public async Task Install_ExactlyFiveRedirects_IsStillAccepted()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, Hex(exe), Sign(key, exe), spki, req =>
        {
            var u = req.RequestUri!.ToString();
            if (u == ExeUrl) return Redirect("https://objects.githubusercontent.com/hop/1");
            if (u.StartsWith("https://objects.githubusercontent.com/hop/", StringComparison.Ordinal))
            {
                var n = int.Parse(u[(u.LastIndexOf('/') + 1)..]);
                return n < 5 ? Redirect("https://objects.githubusercontent.com/hop/" + (n + 1)) : Ok(exe);
            }
            return null;
        });
        try { Assert.True(result.Success); }
        finally { Cleanup(launched); }
    }

    [Theory]
    [InlineData(ExeUrl)]
    [InlineData(ShaUrl)]
    [InlineData(SigUrl)]
    public async Task Install_StalledBody_IsDroppedAfterTheIdleTimeout(string stalledUrl)
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var handler = new FakeHandler(req =>
        {
            var u = req.RequestUri!.ToString();
            if (u == stalledUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            return u switch
            {
                ExeUrl => Ok(exe),
                ShaUrl => Ok(Hex(exe)),
                SigUrl => Ok(Convert.ToBase64String(Sign(key, exe))),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var called = false;
        var work = UpdateService.DownloadAndApplyAsync(SignedRelease(), handler, spki, (_, _, _, _, _) => called = true,
            readFileVersion: _ => new Version(999, 0, 0), installedVersion: new Version(2, 10, 0),
            readIdleTimeout: TimeSpan.FromMilliseconds(300));
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(work, finished);
        var result = await work;
        Assert.False(result.Success);
        Assert.False(called);
    }

    [Fact]
    public async Task Install_OversizedSmallAsset_IsRefused()
    {
        var (key, spki) = NewKey();
        var exe = Exe();
        var (result, launched, _) = await Install(exe, new string('A', 10_000), Sign(key, exe), spki);
        Assert.False(result.Success);
        Assert.Null(launched);
    }

    // ── Script ──

    [Fact]
    public void BuildUpdateScript_WaitLoopIsCapped_AndLogsWhenTheCapIsHit()
    {
        var script = UpdateService.BuildUpdateScript(1234, @"C:\t\run", @"C:\t\run\n.exe", new string('A', 64),
            @"C:\app\IP-Scanner.exe", @"C:\app", @"C:\app\error.log");
        Assert.Contains("set /a WAITS+=1", script);
        Assert.Contains("if %WAITS% gtr 120 goto waitfailed", script);
        Assert.Contains(":waitfailed", script);
        Assert.Contains("did not exit within 120 seconds", script);
        Assert.Equal(120, UpdateService.MaxScriptWaitSeconds);
        // The wait loop must not loop unconditionally.
        Assert.DoesNotContain("  goto wait\r\n", script);
    }

    [Fact]
    public void BuildUpdateScript_WaitFailureDoesNotRelaunchOrSwap()
    {
        var script = UpdateService.BuildUpdateScript(1234, @"C:\t\run", @"C:\t\run\n.exe", new string('A', 64),
            @"C:\app\IP-Scanner.exe", @"C:\app", @"C:\app\error.log");
        var failed = script.IndexOf(":waitfailed", StringComparison.Ordinal);
        var swap = script.IndexOf(":swap", StringComparison.Ordinal);
        var block = script[failed..swap];
        Assert.Contains("goto cleanup", block);
        Assert.DoesNotContain("move /y", block);
        Assert.DoesNotContain("start ", block);
    }
}
