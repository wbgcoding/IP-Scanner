using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace IpScanner.Core;

/// <summary>
/// Checks the GitHub Releases API for a newer build and, once the user agrees,
/// downloads it and swaps the running single-file exe through a tiny updater
/// script. All network calls are best-effort and never throw.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/wbgcoding/IP-Scanner/releases/latest";
    private const string UserAgent = "IP-Scanner-Updater";
    private const string UpdateExeName = "IP-Scanner.update.exe";
    private const string UpdateScriptName = "ip-scanner-update.cmd";

    public sealed record Release(Version Version, string DownloadUrl);

    /// <summary>Installed assembly version (e.g. 2.7.0).</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>The latest release if it is newer than the running build and
    /// ships a downloadable .exe asset; otherwise null (up to date / offline).</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            await using var stream = await http.GetStreamAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                !TryParseVersion(tagEl.GetString(), out var version) ||
                !IsNewer(version, CurrentVersion))
                return null;

            if (!root.TryGetProperty("assets", out var assets)) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (asset.TryGetProperty("browser_download_url", out var u) &&
                    u.GetString() is { Length: > 0 } url)
                    return new Release(version, url);
            }
            return null;
        }
        catch { return null; }   // offline, rate-limited, or unexpected payload
    }

    /// <summary>Download the new exe and launch an updater that waits for this
    /// process to exit, replaces the exe and relaunches it. Returns false on
    /// failure, leaving the running app untouched.</summary>
    public static async Task<bool> DownloadAndApplyAsync(string downloadUrl, CancellationToken ct = default)
    {
        if (!downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        var targetExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(targetExe)) return false;
        var targetDir = Path.GetDirectoryName(targetExe);
        if (string.IsNullOrEmpty(targetDir)) return false;

        try
        {
            var newExe = Path.Combine(Path.GetTempPath(), UpdateExeName);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                var bytes = await http.GetByteArrayAsync(downloadUrl, ct).ConfigureAwait(false);
                if (bytes.Length == 0) return false;
                await File.WriteAllBytesAsync(newExe, bytes, ct).ConfigureAwait(false);
            }
            LaunchUpdater(newExe, targetExe, targetDir);
            return true;
        }
        catch { return false; }
    }

    private static void LaunchUpdater(string newExe, string targetExe, string targetDir)
    {
        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), UpdateScriptName);
        var cmd =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /fi \"PID eq {pid}\" /nh | find \"{pid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"move /y \"{newExe}\" \"{targetExe}\" >nul\r\n" +
            $"cd /d \"{targetDir}\"\r\n" +
            $"start \"\" \"{targetExe}\"\r\n" +
            "del \"%~f0\"\r\n";
        File.WriteAllText(script, cmd);
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>True if the release tag parses to a build newer than <paramref name="current"/>.</summary>
    internal static bool IsNewerTag(string? tag, Version current)
        => TryParseVersion(tag, out var v) && IsNewer(v, current);

    private static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var s = tag.Trim().TrimStart('v', 'V');
        int dots = s.Count(c => c == '.');
        while (dots < 2) { s += ".0"; dots++; }      // normalize to Major.Minor.Build
        return Version.TryParse(s, out version!);
    }

    /// <summary>Compare on Major.Minor.Build only (the assembly version carries a
    /// trailing .0 revision that release tags don't).</summary>
    private static bool IsNewer(Version latest, Version current)
    {
        if (latest.Major != current.Major) return latest.Major > current.Major;
        if (latest.Minor != current.Minor) return latest.Minor > current.Minor;
        return Math.Max(0, latest.Build) > Math.Max(0, current.Build);
    }
}
