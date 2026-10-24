using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using IpScanner.Core.Data;

namespace IpScanner.Core;

/// <summary>The downloaded update did not carry a valid signature from the update signing key.</summary>
public sealed class UpdateSignatureException : Exception
{
    public UpdateSignatureException() : base("update signature is missing or invalid") { }
}

/// <summary>
/// Checks the GitHub Releases API for a newer build and, once the user agrees,
/// downloads it and swaps the running single-file exe through a tiny updater
/// script. All network calls are best-effort and never throw.
/// </summary>
public static class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/wbgcoding/IP-Scanner/releases/latest";
    // The only host an asset URL from the release API is ever allowed to point at.
    // GitHub's own "browser_download_url" for a release always names github.com
    // itself (it 302s to the CDN internally) - anything else in that field,
    // however it got there, is not the expected source and must be refused
    // rather than silently fetched.
    private const string ExpectedAssetHost = "github.com";
    private const string UserAgent = "IP-Scanner-Updater";
    private const string UpdateExeName = "IP-Scanner.update.exe";

    /// <summary>The only release asset the updater installs; a console exe in the same release is never picked.</summary>
    private const string ReleaseExeName = "IP-Scanner.exe";
    private const string UpdateScriptName = "ip-scanner-update.cmd";

    // Hosts a release download may be redirected to; GitHub serves assets from its CDN hosts.
    private static readonly string[] AllowedRedirectHosts =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
    // The release check may be redirected within the API (e.g. after a repository rename).
    private static readonly string[] ApiRedirectHosts = ["api.github.com", "github.com"];
    private const int MaxRedirects = 5;
    private const string FallbackPageUrl = "https://github.com/wbgcoding/IP-Scanner/releases/latest";

    // Update signing public key: SubjectPublicKeyInfo of an ECDSA P-256 key, base64. The matching
    // private key stays on the maintainer's machine (scripts/ReleaseSigner). The placeholder is a
    // value nothing verifies against; it only exists so tests can prove that.
    internal const string PlaceholderPublicKey = "UNSET";
    internal const string UpdatePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEo4g9TxX/ZYMl7z+ppaDWDpqLD/sv6UYOzUoiZCOoZHYs3WAvrlxPTPR+fhD3vIRVpdDskZXY+80rDlb1OWs5Rg==";

    private const int MaxSmallAssetBytes = 4096;

    // With ResponseHeadersRead the HttpClient timeout stops once headers arrive, so the body is
    // guarded here: a read that stays silent this long, or a whole install running this long, is dropped.
    private static readonly TimeSpan DefaultReadIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OverallInstallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A release as the update flow sees it. <see cref="SigUrl"/> is null for a release that
    /// ships no signature; <see cref="PageUrl"/> is the release page on github.com.</summary>
    public sealed record Release(Version Version, string DownloadUrl, string Sha256Url, string? SigUrl, string PageUrl);

    public enum CheckStatus { UpToDate, UpdateAvailable, UpdateAvailableUnsigned, Failed }
    public sealed record CheckResult(CheckStatus Status, Release? Release);

    public enum ApplyFailure { Other, Download, Signature, Folder }

    /// <summary>Outcome of an install attempt; on failure <see cref="Failure"/> names the reason and
    /// <see cref="Error"/> carries the specific exception where there is one.</summary>
    public sealed record ApplyResult(bool Success, Exception? Error = null, ApplyFailure Failure = ApplyFailure.Other);

    // A downloaded exe larger than this is refused outright - the real
    // build is ~56 MB; this only guards against a runaway/malicious response.
    private const long MaxDownloadBytes = 300 * 1024 * 1024;

    /// <summary>Installed assembly version (e.g. 2.7.0).</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Checks GitHub for a newer release. Distinguishes "genuinely up
    /// to date" from "couldn't check" (offline, rate-limited, bad payload) so
    /// callers never report false confidence.</summary>
    public static Task<CheckResult> CheckAsync(CancellationToken ct = default)
        => CheckAsync(new HttpClientHandler { AllowAutoRedirect = false }, ct);

    internal static async Task<CheckResult> CheckAsync(HttpMessageHandler handler, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // The client timeout ends once headers arrive, so the whole check gets its own deadline.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await GetFollowingRedirectsAsync(http, LatestReleaseApi, ApiRedirectHosts, deadline.Token).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, deadline.Token).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) || !TryParseVersion(tagEl.GetString(), out var version))
                return new CheckResult(CheckStatus.Failed, null);
            if (!IsNewer(version, CurrentVersion))
                return new CheckResult(CheckStatus.UpToDate, null);
            if (!root.TryGetProperty("assets", out var assets))
                return new CheckResult(CheckStatus.Failed, null);

            // Pair each .exe with its own .exe.sha256 and .exe.sig by shared base name - never
            // trust "any exe asset" next to "any checksum asset" in the release.
            var exeAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shaAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var sigAssets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !asset.TryGetProperty("browser_download_url", out var u)
                    || u.GetString() is not { Length: > 0 } url) continue;
                // An asset from any other host never enters the pairing below, so it can
                // never be offered as an update source.
                if (!IsTrustedAssetUrl(url)) continue;
                if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
                    shaAssets[name[..^".sha256".Length]] = url;
                else if (name.EndsWith(".exe.sig", StringComparison.OrdinalIgnoreCase))
                    sigAssets[name[..^".sig".Length]] = url;
                else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    exeAssets[name] = url;
            }
            var paired = exeAssets.Keys.FirstOrDefault(k =>
                string.Equals(k, ReleaseExeName, StringComparison.OrdinalIgnoreCase) && shaAssets.ContainsKey(k));
            // No release without a published checksum is ever offered - an
            // unverifiable download is refused, not silently applied.
            if (paired is null) return new CheckResult(CheckStatus.Failed, null);
            var pageUrl = root.TryGetProperty("html_url", out var h) && h.GetString() is { } page && IsTrustedAssetUrl(page)
                ? page : FallbackPageUrl;
            var sigUrl = sigAssets.TryGetValue(paired, out var sg) ? sg : null;
            var release = new Release(version, exeAssets[paired], shaAssets[paired], sigUrl, pageUrl);
            // A release without a signature is announced but never installed from inside the app.
            return new CheckResult(sigUrl is null ? CheckStatus.UpdateAvailableUnsigned : CheckStatus.UpdateAvailable, release);
        }
        catch (Exception ex) when (IsExpectedCheckFailure(ex))
        {
            // Offline, DNS down, timeout or a rate limit: routine on every start without a network.
            Log.Info(LogArea.Update, "Update check skipped: " + ex.GetType().Name);
            return new CheckResult(CheckStatus.Failed, null);
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Update check failed", ex); return new CheckResult(CheckStatus.Failed, null); }   // unexpected payload or a refused redirect
    }

    private static bool IsExpectedCheckFailure(Exception ex) =>
        ex is OperationCanceledException || (ex is HttpRequestException h && h.Message != "redirect refused");

    /// <summary>Download the new exe and launch an updater that waits for this
    /// process to exit, replaces the exe and relaunches it. Nothing is installed
    /// unless the download matches the published checksum AND carries a valid
    /// signature from the update signing key; on any failure the running app is
    /// left untouched.</summary>
    public static Task<ApplyResult> DownloadAndApplyAsync(Release release, CancellationToken ct = default)
        => DownloadAndApplyAsync(release, new HttpClientHandler { AllowAutoRedirect = false }, UpdatePublicKey, LaunchUpdater, ct);

    internal static async Task<ApplyResult> DownloadAndApplyAsync(Release release, HttpMessageHandler handler,
        string publicKeySpkiBase64, Action<string, string, string, string, string> launcher, CancellationToken ct = default,
        Func<string, Version?>? readFileVersion = null, Version? installedVersion = null, TimeSpan? readIdleTimeout = null)
    {
        readFileVersion ??= ReadFileVersion;
        installedVersion ??= CurrentVersion;
        var idle = readIdleTimeout ?? DefaultReadIdleTimeout;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(OverallInstallTimeout);
        ct = overall.Token;
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        string? runDir = null;
        var launched = false;
        try
        {
            // A Release can reach this method from anywhere, not only straight out of
            // CheckAsync, so every URL is validated again here. An unsigned release has no
            // SigUrl and is refused outright.
            if (release.SigUrl is null || !IsTrustedAssetUrl(release.DownloadUrl)
                || !IsTrustedAssetUrl(release.Sha256Url) || !IsTrustedAssetUrl(release.SigUrl))
                return new ApplyResult(false, new UpdateSignatureException(), ApplyFailure.Signature);
            var targetExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(targetExe)) return new ApplyResult(false);
            var targetDir = Path.GetDirectoryName(targetExe);
            if (string.IsNullOrEmpty(targetDir)) return new ApplyResult(false);
            // All paths are dropped straight into the generated .cmd inside "..." quoting; a
            // stray " breaks out of it and a % is cmd.exe's variable expansion.
            var plannedRunDir = Path.Combine(Path.GetTempPath(), "ip-scanner-update-" + Guid.NewGuid().ToString("N"));
            var newExe = Path.Combine(plannedRunDir, UpdateExeName);
            if (!IsSafeScriptPath(targetExe) || !IsSafeScriptPath(targetDir)
                || !IsSafeScriptPath(plannedRunDir) || !IsSafeScriptPath(newExe))
                return new ApplyResult(false);

            if (!IsFolderWritable(targetDir)) return new ApplyResult(false, null, ApplyFailure.Folder);

            // A fixed name under the shared %TEMP% is a TOCTOU window between verification and
            // the swap; a freshly named per-run directory restricted to the current user closes
            // it. If the restriction cannot be applied the update is aborted.
            runDir = plannedRunDir;
            if (!FileProtection.PrepareDirectory(runDir)) return new ApplyResult(false);

            var expectedHash = await FetchHashAsync(http, release.Sha256Url, idle, ct).ConfigureAwait(false);
            var signature = await FetchSignatureAsync(http, release.SigUrl, idle, ct).ConfigureAwait(false);
            if (expectedHash is null) return new ApplyResult(false, null, ApplyFailure.Download);
            if (signature is null) return new ApplyResult(false, new UpdateSignatureException(), ApplyFailure.Signature);

            var digest = await DownloadToFileAsync(http, release.DownloadUrl, newExe, idle, ct).ConfigureAwait(false);
            if (digest is null) return new ApplyResult(false, null, ApplyFailure.Download);
            var actualHash = Convert.ToHexString(digest);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return new ApplyResult(false, null, ApplyFailure.Download);

            // The signature is checked against the very digest of the streamed bytes that the
            // checksum was compared with, so both checks cover the same data; the updater script
            // later binds that digest to the file on disk.
            if (!VerifyDigestSignature(digest, signature, publicKeySpkiBase64))
                return new ApplyResult(false, new UpdateSignatureException(), ApplyFailure.Signature);

            // A signature proves who built the file, not which version it is: an older genuinely
            // signed exe uploaded under a new tag would otherwise pass. The embedded version must be
            // the announced one and newer than the running build.
            if (!IsExpectedUpdateVersion(readFileVersion(newExe), release.Version, installedVersion))
                return new ApplyResult(false, new UpdateSignatureException(), ApplyFailure.Signature);

            // The updater script re-hashes the file on disk right before the swap, the one
            // moment the gap between this check and the swap matters.
            launcher(runDir, newExe, actualHash, targetExe, targetDir);
            launched = true;
            return new ApplyResult(true);
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Applying the update failed", ex); return new ApplyResult(false, null, ClassifyFailure(ex)); }
        finally
        {
            if (!launched && runDir is not null) TryRemoveRunDir(runDir);
        }
    }

    /// <summary>Only a failed transfer is reported as a download problem; a full disk, a denied
    /// temp folder or anything else local must not send the user to check the internet connection.</summary>
    internal static ApplyFailure ClassifyFailure(Exception ex) => ex switch
    {
        UnauthorizedAccessException => ApplyFailure.Other,
        IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 => ApplyFailure.Other,
        HttpRequestException or OperationCanceledException or IOException or InvalidDataException => ApplyFailure.Download,
        _ => ApplyFailure.Other,
    };

    /// <summary>True when <paramref name="signature"/> (raw IEEE P1363 r||s) is a valid ECDSA
    /// P-256 / SHA-256 signature over the bytes of <paramref name="exe"/> for the given
    /// SubjectPublicKeyInfo key (base64). An unusable key or signature is a refusal, never a throw.</summary>
    internal static bool VerifySignature(Stream exe, byte[] signature, string spkiBase64)
    {
        try { return VerifyDigestSignature(SHA256.HashData(exe), signature, spkiBase64); }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Update signature check failed", ex); return false; }
    }

    /// <summary>Same check as <see cref="VerifySignature"/> for an already computed SHA-256 digest.</summary>
    internal static bool VerifyDigestSignature(byte[] sha256Digest, byte[] signature, string spkiBase64)
    {
        try
        {
            if (sha256Digest.Length != 32 || signature.Length == 0 || string.IsNullOrWhiteSpace(spkiBase64)) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(spkiBase64), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyHash(sha256Digest, signature);
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Update signature check failed", ex); return false; }
    }

    private static Version? ReadFileVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Reading the update data failed", ex); return null; }
    }

    /// <summary>True when the downloaded exe's embedded version (Major.Minor.Build) equals the announced
    /// release version and is newer than the installed build.</summary>
    internal static bool IsExpectedUpdateVersion(Version? embedded, Version announced, Version installed) =>
        embedded is not null
        && embedded.Major == announced.Major && embedded.Minor == announced.Minor
        && Math.Max(0, embedded.Build) == Math.Max(0, announced.Build)
        && IsNewer(embedded, installed);

    private static void TryRemoveRunDir(string runDir)
    {
        try
        {
            // Own per-run folder: its few files go first, the folder itself only when empty.
            foreach (var f in Directory.EnumerateFiles(runDir)) File.Delete(f);
            Directory.Delete(runDir, recursive: false);
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Update cleanup failed", ex); /* best-effort */ }
    }

    /// <summary>GET with redirects followed by hand: at most <see cref="MaxRedirects"/> hops, each
    /// target https on an allowed GitHub host. The returned response is a success; the caller
    /// disposes it.</summary>
    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(HttpClient http, string url, string[] allowedHosts, CancellationToken ct)
    {
        var current = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!IsRedirect(resp.StatusCode))
            {
                try { resp.EnsureSuccessStatusCode(); return resp; }
                catch { resp.Dispose(); throw; }
            }
            var location = resp.Headers.Location;
            resp.Dispose();
            if (hop >= MaxRedirects || location is null) throw new HttpRequestException("redirect refused");
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedRedirectTarget(next, allowedHosts)) throw new HttpRequestException("redirect refused");
            current = next;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode code) =>
        code is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    internal static bool IsAllowedRedirectTarget(Uri uri) => IsAllowedRedirectTarget(uri, AllowedRedirectHosts);

    private static bool IsAllowedRedirectTarget(Uri uri, string[] allowedHosts) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && allowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream src, byte[] buffer, TimeSpan idle, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(idle);
        return await src.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false);
    }

    /// <summary>Downloads a small text asset (checksum or signature) with a hard cap.</summary>
    private static async Task<string> FetchSmallTextAsync(HttpClient http, string url, TimeSpan idle, CancellationToken ct)
    {
        using var resp = await GetFollowingRedirectsAsync(http, url, AllowedRedirectHosts, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength is { } len && len > MaxSmallAssetBytes)
            throw new InvalidDataException("asset too large");
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await ReadWithIdleTimeoutAsync(src, chunk, idle, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSmallAssetBytes) throw new InvalidDataException("asset too large");
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Published checksum (uppercase hex, 64 digits) or null when malformed.</summary>
    private static async Task<string?> FetchHashAsync(HttpClient http, string shaUrl, TimeSpan idle, CancellationToken ct)
    {
        var text = await FetchSmallTextAsync(http, shaUrl, idle, ct).ConfigureAwait(false);
        var hex = new string(text.Trim().TakeWhile(Uri.IsHexDigit).ToArray());
        return hex.Length == 64 ? hex.ToUpperInvariant() : null;
    }

    /// <summary>Published signature bytes (the .sig asset is base64) or null when malformed.</summary>
    private static async Task<byte[]?> FetchSignatureAsync(HttpClient http, string sigUrl, TimeSpan idle, CancellationToken ct)
    {
        var text = await FetchSmallTextAsync(http, sigUrl, idle, ct).ConfigureAwait(false);
        try { return Convert.FromBase64String(text.Trim()); }
        catch (FormatException) { return null; }
    }

    /// <summary>Streams the download straight into <paramref name="path"/> with a hard size cap
    /// (honored while streaming, so a dishonest or missing Content-Length cannot force an
    /// unbounded read) and returns the SHA-256 digest of what was written, or null when
    /// the cap was exceeded or the body was empty.</summary>
    private static async Task<byte[]?> DownloadToFileAsync(HttpClient http, string url, string path, TimeSpan idle, CancellationToken ct)
    {
        using var resp = await GetFollowingRedirectsAsync(http, url, AllowedRedirectHosts, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentLength is { } len && len > MaxDownloadBytes) return null;

        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await ReadWithIdleTimeoutAsync(src, chunk, idle, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes) return null;
            hasher.AppendData(chunk, 0, read);
            await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        if (total == 0) return null;
        await file.FlushAsync(ct).ConfigureAwait(false);
        return hasher.GetHashAndReset();
    }

    /// <summary>True when a file can be created in <paramref name="dir"/>, which the swap needs.</summary>
    internal static bool IsFolderWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Update verification failed", ex); return false; }
    }

    private static void LaunchUpdater(string runDir, string newExe, string expectedHash, string targetExe, string targetDir)
    {
        var pid = Environment.ProcessId;
        // Same file/name convention as App.ErrorLogPath (AppContext.BaseDirectory
        // is the exe's own folder at runtime, i.e. targetDir here) - a failed
        // swap becomes visible through the app's existing "open log" button
        // instead of vanishing silently.
        var errorLog = Path.Combine(targetDir, "error.log");
        // The script lives in the same per-run, current-user-only directory as newExe.
        var script = Path.Combine(runDir, UpdateScriptName);
        try
        {
            // The script appends to this log; a file it would create itself gets the folder's inherited ACL.
            if (!File.Exists(errorLog))
            {
                File.WriteAllBytes(errorLog, Array.Empty<byte>());
                FileProtection.RestrictFile(errorLog);
            }
        }
        catch (Exception ex) { Log.Warn(LogArea.Update, "Writing the update script report failed", ex); /* the script reports into whatever the folder allows */ }
        File.WriteAllText(script, BuildUpdateScript(pid, runDir, newExe, expectedHash, targetExe, targetDir, errorLog));
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = ScriptLaunchArguments(script),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>cmd strips the outer quote pair when the path holds &amp; ( ) and similar, so an
    /// extra pair keeps the inner quotes intact; /d skips AutoRun, /v:off keeps ! literal.</summary>
    internal static string ScriptLaunchArguments(string script) => $"/d /v:off /c \"\"{script}\"\"";

    private const string HashFileName = "update.sha256";

    /// <summary>Longest the updater script waits for the app to exit before it gives up.</summary>
    internal const int MaxScriptWaitSeconds = 120;

    /// <summary>Builds the updater script: waits for this process to exit, re-hashes the new exe
    /// and swaps it in, logging any failure.</summary>
    internal static string BuildUpdateScript(int pid, string runDir, string newExe, string expectedHash,
        string targetExe, string targetDir, string errorLogPath) =>
        // Tools are called by full System32 path: Git/WSL/Cygwin earlier on PATH ship a different "find".
        "@echo off\r\n" +
        @"set SYS32=%SystemRoot%\System32" + "\r\n" +
        "set /a WAITS=0\r\n" +
        ":wait\r\n" +
        $"\"%SYS32%\\tasklist.exe\" /fi \"PID eq {pid}\" /nh | \"%SYS32%\\find.exe\" \"{pid}\" >nul\r\n" +
        "if errorlevel 1 goto swap\r\n" +
        "set /a WAITS+=1\r\n" +
        $"if %WAITS% gtr {MaxScriptWaitSeconds} goto waitfailed\r\n" +
        "\"%SYS32%\\timeout.exe\" /t 1 /nobreak >nul\r\n" +
        "goto wait\r\n" +
        ":waitfailed\r\n" +
        $"echo [update] %date% %time% update aborted: the app did not exit within {MaxScriptWaitSeconds} seconds >> \"{errorLogPath}\"\r\n" +
        "goto cleanup\r\n" +
        ":swap\r\n" +
        "set ACTUALHASH=\r\n" +
        // The hash goes through a file: a command inside for /f ('...') loses its quoting
        // as soon as a path holds ( ) or &, while a usebackq file name stays quoted.
        $"\"%SYS32%\\certutil.exe\" -hashfile \"{newExe}\" SHA256 > \"{Path.Combine(runDir, HashFileName)}\" 2>nul\r\n" +
        $"for /f \"usebackq skip=1 delims=\" %%h in (\"{Path.Combine(runDir, HashFileName)}\") do if not defined ACTUALHASH set ACTUALHASH=%%h\r\n" +
        $"if /i not \"%ACTUALHASH%\"==\"{expectedHash}\" (\r\n" +
        $"  echo [update] %date% %time% swap aborted: downloaded file's checksum changed since it was verified >> \"{errorLogPath}\"\r\n" +
        $"  if exist \"{targetExe}\" start \"\" \"{targetExe}\"\r\n" +
        "  goto cleanup\r\n" +
        ")\r\n" +
        // move/del/start/echo/if/goto are cmd.exe built-ins, resolved
        // internally before any PATH lookup - never shadowable, no need to
        // qualify those.
        $"move /y \"{newExe}\" \"{targetExe}\" >nul\r\n" +
        "if errorlevel 1 (\r\n" +
        $"  echo [update] %date% %time% swap failed: could not move \"{EchoText(newExe)}\" to \"{EchoText(targetExe)}\" >> \"{errorLogPath}\"\r\n" +
        $"  if exist \"{targetExe}\" start \"\" \"{targetExe}\"\r\n" +
        "  goto cleanup\r\n" +
        ")\r\n" +
        $"if not exist \"{targetExe}\" (\r\n" +
        $"  echo [update] %date% %time% swap failed: \"{EchoText(targetExe)}\" missing after move >> \"{errorLogPath}\"\r\n" +
        "  goto cleanup\r\n" +
        ")\r\n" +
        $"cd /d \"{targetDir}\"\r\n" +
        $"start \"\" \"{targetExe}\"\r\n" +
        ":cleanup\r\n" +
        "cd /d \"%TEMP%\"\r\n" +
        $"del \"{newExe}\" >nul 2>&1\r\n" +
        $"del \"{Path.Combine(runDir, HashFileName)}\" >nul 2>&1\r\n" +
        $"del /f /q /a:h \"{Path.Combine(runDir, Data.FileProtection.OwnerMarkerName)}\" >nul 2>&1\r\n" +
        // One parenthesized line is parsed before it runs, so the rmdir still
        // executes after the script has deleted itself.
        $"(del \"%~f0\" >nul 2>&1 & rmdir \"{runDir}\" >nul 2>&1)\r\n";

    /// <summary>A path as it appears in a logged echo line: redacted like every other log entry, with
    /// the percent signs the redaction introduces doubled so cmd.exe prints them instead of expanding them.</summary>
    private static string EchoText(string path) => LogRedactor.Redact(path).Replace("%", "%%");

    /// <summary>True only when <paramref name="path"/> is safe to embed
    /// into the generated .cmd script - no double quote (would break out of the
    /// script's own "..." quoting) and no percent sign (cmd.exe variable
    /// expansion, e.g. a folder named "50%Off" reads as "50" + the value of
    /// %Off%).</summary>
    internal static bool IsSafeScriptPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.IndexOfAny(['"', '%']) < 0;

    /// <summary>True only for an https URL whose host is exactly
    /// <see cref="ExpectedAssetHost"/> - a plain string prefix check like
    /// "StartsWith https://" would accept e.g.
    /// "https://github.com.evil.example/..." just as happily.</summary>
    internal static bool IsTrustedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Host, ExpectedAssetHost, StringComparison.OrdinalIgnoreCase);

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
