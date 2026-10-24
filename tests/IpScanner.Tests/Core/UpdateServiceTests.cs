using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using IpScanner.Core;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("2.8.0", true)]      // newer build
    [InlineData("v2.8", true)]       // 'v' prefix, missing build
    [InlineData("3.0", true)]        // newer major
    [InlineData("2.7.0", false)]     // same version
    [InlineData("2.7", false)]       // same, short form
    [InlineData("2.6.9", false)]     // older
    [InlineData("1.0.0", false)]     // older major
    [InlineData("", false)]          // empty tag
    [InlineData("not-a-version", false)]
    public void IsNewerTag_ComparesAgainstCurrent(string tag, bool expected)
        => Assert.Equal(expected, UpdateService.IsNewerTag(tag, new Version(2, 7, 0, 0)));

    [Theory]
    [InlineData("https://github.com/wbgcoding/IP-Scanner/releases/download/v2.8.0/IP-Scanner.exe", true)]
    [InlineData("http://github.com/wbgcoding/IP-Scanner/releases/download/v2.8.0/IP-Scanner.exe", false)]   // not https
    [InlineData("https://evil.example/IP-Scanner.exe", false)]                          // wrong host entirely
    [InlineData("https://github.com.evil.example/IP-Scanner.exe", false)]               // a bare "StartsWith https://" check would have accepted this host
    [InlineData("https://evil.github.com.attacker.io/IP-Scanner.exe", false)]           // github.com as a subdomain prefix trick
    [InlineData("https://github.com@evil.example/IP-Scanner.exe", false)]               // userinfo trick - real host is evil.example
    [InlineData("https://user:pw@github.com/wbgcoding/IP-Scanner/x.exe", false)]       // userinfo in front of the right host
    [InlineData("https://github.com:8443/wbgcoding/IP-Scanner/x.exe", false)]          // non-default port
    [InlineData("https://github.com:443/wbgcoding/IP-Scanner/x.exe", true)]            // explicit default port is the same address
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTrustedAssetUrl_OnlyAcceptsPlainHttpsGithubCom(string? url, bool expected)
        => Assert.Equal(expected, UpdateService.IsTrustedAssetUrl(url));

    [Theory]
    [InlineData(@"C:\Program Files\IP-Scanner\IP-Scanner.exe", true)]
    [InlineData(@"C:\Tools\50% Off Tools\IP-Scanner.exe", false)]   // % - cmd.exe variable expansion
    [InlineData("C:\\weird\"path\\IP-Scanner.exe", false)]              // " - breaks the script's own quoting
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSafeScriptPath_RejectsQuotesAndPercentSigns(string? path, bool expected)
        => Assert.Equal(expected, UpdateService.IsSafeScriptPath(path));

    // A file existing at targetExe afterwards is true whether the move succeeded or failed
    // and left the old exe untouched. These two tests execute the generated .cmd (a real
    // functional test of the batch logic, not a string-content assertion) against a locked
    // vs. unlocked target file, proving the script distinguishes the two cases.
    // A guaranteed-not-running PID skips the "wait for this process to exit"
    // loop instantly; .cmd stand-ins (not real .exe files) keep the script's
    // own "start" launch instant and harmless; their trailing "exit" closes the console
    // window that "start" opens for a batch file, so no window stays behind.
    private const int NeverRunningPid = 999999999;

    private static void RunScriptAndWait(string script)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = UpdateService.ScriptLaunchArguments(script),
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        Assert.True(p.WaitForExit(10_000), "update script did not finish within 10s");
    }

    [Fact]
    public void BuildUpdateScript_PathWithParenthesesAndAmpersand_SwapsAndRemovesRunFolder()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var runDir = Path.Combine(root, "ip-scanner-update-a (b) & c");
        var targetDir = Path.Combine(root, "app");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(targetDir);
        var marker = Path.Combine(runDir, FileProtection.OwnerMarkerName);
        File.WriteAllText(marker, "");
        File.SetAttributes(marker, FileAttributes.Hidden);
        var newExe = Path.Combine(runDir, "new.cmd");
        var targetExe = Path.Combine(targetDir, "target.cmd");
        var errorLog = Path.Combine(targetDir, "error.log");
        File.WriteAllText(newExe, "@echo off\r\nrem NEW-BUILD-MARKER\r\nexit\r\n");
        File.WriteAllText(targetExe, "@echo off\r\nrem OLD-BUILD-MARKER\r\nexit\r\n");

        var script = Path.Combine(runDir, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(NeverRunningPid, runDir, newExe, Sha256Hex(newExe), targetExe, targetDir, errorLog));
        RunScriptAndWait(script);

        Assert.Contains("NEW-BUILD-MARKER", File.ReadAllText(targetExe));
        Assert.False(File.Exists(errorLog));
        Assert.False(Directory.Exists(runDir)); // script, marker and folder all gone
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void BuildUpdateScript_UnlockedTarget_MovesNewExeIntoPlace_NoFailureLogged()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var newExe = Path.Combine(dir, "new.cmd");
        var targetExe = Path.Combine(dir, "target.cmd");
        var errorLog = Path.Combine(dir, "error.log");
        File.WriteAllText(newExe, "@echo off\r\nrem NEW-BUILD-MARKER\r\nexit\r\n");
        File.WriteAllText(targetExe, "@echo off\r\nrem OLD-BUILD-MARKER\r\nexit\r\n");
        var hash = Sha256Hex(newExe);

        var script = Path.Combine(dir, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(NeverRunningPid, dir, newExe, hash, targetExe, dir, errorLog));
        RunScriptAndWait(script);

        Assert.Contains("NEW-BUILD-MARKER", File.ReadAllText(targetExe));
        Assert.False(File.Exists(newExe));     // moved away, not left behind
        Assert.False(File.Exists(errorLog));   // nothing went wrong -> no log entry
        Assert.False(File.Exists(script));     // self-deleted
    }

    [Fact]
    public void BuildUpdateScript_LockedTarget_LeavesOldExeAndLogsFailure_InsteadOfReportingSuccess()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var newExe = Path.Combine(dir, "new.cmd");
        var targetExe = Path.Combine(dir, "target.cmd");
        var errorLog = Path.Combine(dir, "error.log");
        File.WriteAllText(newExe, "@echo off\r\nrem NEW-BUILD-MARKER\r\nexit\r\n");
        File.WriteAllText(targetExe, "@echo off\r\nrem OLD-BUILD-MARKER\r\nexit\r\n");
        var hash = Sha256Hex(newExe);

        var script = Path.Combine(dir, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(NeverRunningPid, dir, newExe, hash, targetExe, dir, errorLog));

        // Deny-write/delete share on the target simulates the real-world cause
        // (AV scan, Explorer preview, a second instance) that makes "move"
        // fail - readable (so the fallback "start" of the surviving old exe
        // still works), but not deletable/replaceable.
        using (new FileStream(targetExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RunScriptAndWait(script);

            Assert.Contains("OLD-BUILD-MARKER", File.ReadAllText(targetExe));   // untouched, not silently swapped
        }

        Assert.True(File.Exists(errorLog), "a failed swap must be logged, not reported as success");
        Assert.Contains("swap failed", File.ReadAllText(errorLog));
    }

    [Fact]
    public void BuildUpdateScript_ChecksumChangedSinceVerification_RefusesToApply()
    {
        // Proves the re-verify-before-swap gate itself, independent of
        // the "move fails" case above - a file that was swapped out after
        // DownloadAndApplyAsync verified it (the whole point of the per-run,
        // current-user-only directory) must never reach the target, even
        // though the move itself would otherwise succeed cleanly.
        var dir = Directory.CreateTempSubdirectory().FullName;
        var newExe = Path.Combine(dir, "new.cmd");
        var targetExe = Path.Combine(dir, "target.cmd");
        var errorLog = Path.Combine(dir, "error.log");
        File.WriteAllText(newExe, "@echo off\r\nrem TAMPERED-AFTER-VERIFY\r\n");
        File.WriteAllText(targetExe, "@echo off\r\nrem OLD-BUILD-MARKER\r\nexit\r\n");
        var staleHash = new string('a', 64);   // does not match newExe's real content

        var script = Path.Combine(dir, "update.cmd");
        File.WriteAllText(script, UpdateService.BuildUpdateScript(NeverRunningPid, dir, newExe, staleHash, targetExe, dir, errorLog));
        RunScriptAndWait(script);

        Assert.Contains("OLD-BUILD-MARKER", File.ReadAllText(targetExe));   // never swapped in
        Assert.True(File.Exists(errorLog), "a refused swap must be logged");
        Assert.Contains("checksum changed", File.ReadAllText(errorLog));
    }

    [Fact]
    public void BuildUpdateScript_EchoLinesCarryNoUserPath_WhileTheCommandsKeepTheRealOnes()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runDir = Path.Combine(profile, "AppData", "Local", "Temp", "ip-scanner-update-abc");
        var newExe = Path.Combine(runDir, "IP-Scanner.exe");
        var targetExe = Path.Combine(profile, "tools", "IP-Scanner.exe");
        var script = UpdateService.BuildUpdateScript(NeverRunningPid, runDir, newExe, new string('a', 64),
            targetExe, Path.GetDirectoryName(targetExe)!, Path.Combine(profile, "tools", "error.log"));

        var echoes = script.Split("\r\n").Where(l => l.TrimStart().StartsWith("echo [update]")).ToList();
        Assert.NotEmpty(echoes);
        var moved = echoes.Single(l => l.Contains("could not move"));
        moved = moved[..moved.LastIndexOf(" >> ", StringComparison.Ordinal)];   // the redirect target is a command, not log text
        Assert.DoesNotContain(profile, moved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%%USERPROFILE%%", moved);
        Assert.Contains($"move /y \"{newExe}\" \"{targetExe}\"", script);
    }

    [Fact]
    public void ClassifyFailure_OnlyATransferProblemIsADownloadFailure()
    {
        Assert.Equal(UpdateService.ApplyFailure.Download, UpdateService.ClassifyFailure(new System.Net.Http.HttpRequestException()));
        Assert.Equal(UpdateService.ApplyFailure.Download, UpdateService.ClassifyFailure(new OperationCanceledException()));
        Assert.Equal(UpdateService.ApplyFailure.Other, UpdateService.ClassifyFailure(new UnauthorizedAccessException()));
        Assert.Equal(UpdateService.ApplyFailure.Other, UpdateService.ClassifyFailure(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.Equal(UpdateService.ApplyFailure.Other, UpdateService.ClassifyFailure(new InvalidOperationException()));
    }
}
