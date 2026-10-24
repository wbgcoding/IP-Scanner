using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Localization;

namespace IpScanner.ViewModels.Tools;

/// <summary>Certificate-check tool page state: connects with TLS, records
/// whatever <see cref="SslPolicyErrors"/> come back instead of rejecting on them - see
/// docs/RULES.md's "Network I/O" entry for why that is deliberate here and nowhere
/// else - and shows the certificate's own details in plain words.</summary>
public sealed class CertificateToolViewModel : ObservableObject
{
    private const int TimeoutMs = 10000;
    private CancellationTokenSource? _cts;

    private string _target = "";
    public string Target
    {
        get => _target;
        set { if (_target != value) { _target = value; Raise(nameof(Target)); Raise(nameof(CanStart)); } }
    }

    private string _port = "443";
    public string Port { get => _port; set { if (_port != value) { _port = value; Raise(nameof(Port)); } } }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (_isBusy != value) { _isBusy = value; Raise(nameof(IsBusy)); Raise(nameof(CanStart)); } } }

    private string _status = "";
    public string Status { get => _status; set { if (_status != value) { _status = value; Raise(nameof(Status)); } } }

    public bool CanStart => !IsBusy && Target.Trim().Length > 0;
    public bool HasResult { get; private set; }

    public string Subject { get; private set; } = "";
    public string Issuer { get; private set; } = "";
    public string ValidFrom { get; private set; } = "";
    public string ValidTo { get; private set; } = "";
    public string DaysRemainingText { get; private set; } = "";
    /// <summary>A theme brush key ("Red"/"Peach"/"Text") for <see cref="DaysRemainingText"/> - the
    /// view resolves it via DynamicResource, same brushes the row table already uses for state.</summary>
    public string DaysRemainingColorKey { get; private set; } = "Text";
    public string SignatureAlgorithm { get; private set; } = "";
    public string KeySize { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public IReadOnlyList<string> SubjectAlternativeNames { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> ChainFindings { get; private set; } = Array.Empty<string>();

    public void Stop() => _cts?.Cancel();

    public async Task StartAsync()
    {
        if (!CanStart) return;
        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            Status = Loc.CertInvalidPort;
            return;
        }

        HasResult = false;
        Status = "";
        Raise(nameof(HasResult));
        IsBusy = true;
        _cts = new CancellationTokenSource(TimeoutMs);
        var ct = _cts.Token;
        var host = Target.Trim();

        try
        {
            SslPolicyErrors captured = SslPolicyErrors.None;
            X509Certificate2? cert = null;

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, ct);
            using var ssl = new SslStream(client.GetStream(), false, (_, c, _, errors) =>
            {
                captured = errors;
                if (c is X509Certificate2 c2) cert = c2;
                else if (c is not null) cert = new X509Certificate2(c);
                return true;   // inspection tool: record the errors, don't refuse the handshake
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = host, EnabledSslProtocols = System.Security.Authentication.SslProtocols.None }, ct);

            if (cert is null) { Status = Loc.CertConnectFailed; return; }
            ApplyResult(cert, captured);
        }
        catch (OperationCanceledException) { Status = Loc.CertConnectFailed; }
        catch (SocketException) { Status = Loc.CertConnectFailed; }
        catch (AuthenticationException) { Status = Loc.CertConnectFailed; }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ApplyResult(X509Certificate2 cert, SslPolicyErrors errors)
    {
        Subject = cert.Subject;
        Issuer = cert.Issuer;
        ValidFrom = cert.NotBefore.ToString("d", CultureInfo.CurrentCulture);
        ValidTo = cert.NotAfter.ToString("d", CultureInfo.CurrentCulture);

        int days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
        if (days < 0) { DaysRemainingText = Loc.CertExpiredMsg(-days); DaysRemainingColorKey = "Red"; }
        else if (days < 14) { DaysRemainingText = Loc.CertDaysRemainingMsg(days); DaysRemainingColorKey = "Peach"; }
        else { DaysRemainingText = Loc.CertDaysRemainingMsg(days); DaysRemainingColorKey = "Text"; }

        SignatureAlgorithm = cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "";
        KeySize = KeySizeOf(cert) is { } size ? size.ToString(CultureInfo.InvariantCulture) : "";
        Fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        SubjectAlternativeNames = ParseSans(cert);
        ChainFindings = DescribeChain(errors);
        HasResult = true;

        Raise(nameof(Subject)); Raise(nameof(Issuer)); Raise(nameof(ValidFrom)); Raise(nameof(ValidTo));
        Raise(nameof(DaysRemainingText)); Raise(nameof(DaysRemainingColorKey)); Raise(nameof(SignatureAlgorithm));
        Raise(nameof(KeySize)); Raise(nameof(Fingerprint)); Raise(nameof(SubjectAlternativeNames));
        Raise(nameof(ChainFindings)); Raise(nameof(HasResult));
    }

    private static int? KeySizeOf(X509Certificate2 cert)
        => cert.GetRSAPublicKey()?.KeySize ?? cert.GetECDsaPublicKey()?.KeySize ?? cert.GetDSAPublicKey()?.KeySize;

    internal static IReadOnlyList<string> ParseSans(X509Certificate2 cert)
    {
        // Read the names ourselves: the system formatter labels them in the Windows display language.
        if (cert.Extensions["2.5.29.17"] is not { } ext) return Array.Empty<string>();
        var san = new X509SubjectAlternativeNameExtension(ext.RawData, ext.Critical);
        return san.EnumerateDnsNames().Select(n => "DNS: " + n)
            .Concat(san.EnumerateIPAddresses().Select(ip => "IP: " + ip))
            .ToArray();
    }

    internal static IReadOnlyList<string> DescribeChain(SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return new[] { Loc.CertChainValid };
        var findings = new List<string>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) findings.Add(Loc.CertChainNoCertificate);
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) findings.Add(Loc.CertChainNameMismatch);
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) findings.Add(Loc.CertChainUntrusted);
        return findings;
    }
}
