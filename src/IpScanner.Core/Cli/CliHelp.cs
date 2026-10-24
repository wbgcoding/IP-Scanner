using System.Text;
using IpScanner.Core.Localization;
using IpScanner.Core.Scanner;

namespace IpScanner.Core.Cli;

/// <summary>The exit codes of a command line run.</summary>
public static class CliExit
{
    public const int Ok = 0;
    public const int ScanError = 1;
    public const int UsageError = 2;
    public const int OutputClosed = 3;
    public const int Cancelled = 130;

    public static readonly IReadOnlyList<(int Code, string Meaning)> Table =
    [
        (Ok, "The scan finished."),
        (ScanError, "The scan could not run or failed (no network, driver missing, file not writable)."),
        (UsageError, "Wrong usage: unknown option, bad value, no target, or a safety gate was not opened."),
        (OutputClosed, "Stdout was closed before the report was printed (for example by | head); report files were still written."),
        (Cancelled, "Stopped with Ctrl+C; the partial result was still written."),
    ];
}

/// <summary>The help text, generated from the flag table.</summary>
public static class CliHelp
{
    /// <summary>Command lines (without the program name) shown in the help. Every one must parse.</summary>
    public static readonly IReadOnlyList<(string Args, string Description)> Examples =
    [
        ("-sn 192.0.2.0/28", "Find the live hosts of a subnet."),
        ("-p 22,80,443 -oJ out.json 192.0.2.10", "Scan three ports and write a JSON report."),
        ("-F -sV 192.0.2.1-20", "Scan the 100 most common ports and identify the services."),
        ("-sU --top-ports 50 192.0.2.10", "Scan the 50 most common UDP ports."),
        ("-sS -p- --allow-full-scan 192.0.2.10", "SYN scan of all 65535 ports (packet driver needed)."),
        ("-iL hosts.txt --exclude 192.0.2.1 -oX scan.xml", "Read targets from a file, leave one out, write XML."),
        ("-sL 192.0.2.0/30", "List the addresses and their names without sending anything."),
        ("-Pn -T4 --open --reason 192.0.2.10", "Skip discovery, scan faster, show only open ports and why."),
        ("--checks safe,discovery 192.0.2.10", "Scan, then run the safe and discovery service checks."),
        ("-O -sV 192.0.2.10", "Estimate the operating system and identify the services (packet driver needed)."),
        ("-oN - -oG hosts.gnmap 192.0.2.0/24", "Text report on stdout, grepable report in a file."),
        ("-6 -p 22,80,443 2001:db8::10", "Scan an IPv6 host."),
    ];

    /// <summary>The purpose paragraph, always in English for the command line.</summary>
    public static string Purpose() => Loc.InEnglish(() => Loc.HelpAboutText);

    /// <summary>The legal note, always in English for the command line.</summary>
    public static string LegalNote() => Loc.InEnglish(() => Loc.HelpLegalNote);

    public static string Text(string program)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{program} {Version()} - network scanner for the command line");
        sb.AppendLine();
        sb.AppendLine(Purpose());
        sb.AppendLine(LegalNote());
        sb.AppendLine();
        sb.AppendLine($"Usage: {program} [options] <target>...");
        sb.AppendLine();
        sb.AppendLine("Targets: a single address, a host name, a CIDR block (192.168.1.0/24), a range");
        sb.AppendLine("(192.168.1.10-50), per-octet ranges (10.0.0-3.1-254), or a list of these separated");
        sb.AppendLine("by commas or spaces. Without -sn, -sL or -Pn the live hosts get a port scan of the");
        sb.AppendLine("1000 most common ports (TCP connect).");
        sb.AppendLine("IPv6 targets (2001:db8::10, fe80::1%12, 2001:db8::/112) need -6 and are scanned with");
        sb.AppendLine("connect and UDP scans only; the raw scan types, -O and the ARP based discovery are IPv4 only.");
        sb.AppendLine("IPv6 support by scan type: " + string.Join(", ", ScanTypes.All.Select(t => ScanTypes.Label(t) + (Capabilities.SupportsIpv6(t) ? " yes" : " no"))) + ".");
        sb.AppendLine();
        sb.AppendLine("Output goes to the console the program was started from. cmd.exe and PowerShell do not wait for a window");
        sb.AppendLine("program, so the prompt may return before the output and the exit code is not set.");
        sb.AppendLine("cmd: `start /wait IP-Scanner.exe <options>` waits and sets %errorlevel%, but cannot be combined with `>`");
        sb.AppendLine("(the redirect would apply to start). To save the output, run `IP-Scanner.exe <options> > report.txt`");
        sb.AppendLine("without start, from a batch file, or use -oN <file>.");
        sb.AppendLine("PowerShell: `>` writes an empty file; pipe instead: `IP-Scanner.exe <options> | Out-File report.txt` or");
        sb.AppendLine("`| Out-Host`. A pipe makes PowerShell wait and sets $LASTEXITCODE.");

        int width = CliFlags.All.Max(f => f.Display.Length) + 2;
        foreach (var group in CliFlags.Groups)
        {
            var flags = CliFlags.All.Where(f => f.Group == group).ToList();
            if (flags.Count == 0) continue;
            sb.AppendLine();
            sb.AppendLine(group + ":");
            foreach (var f in flags)
                sb.AppendLine($"  {f.Display.PadRight(width)}{f.Help}");
        }

        sb.AppendLine();
        sb.AppendLine("Examples:");
        foreach (var (args, description) in Examples)
        {
            sb.AppendLine($"  {program} {args}");
            sb.AppendLine($"      {description}");
        }

        sb.AppendLine();
        sb.AppendLine("Exit codes:");
        foreach (var (code, meaning) in CliExit.Table)
            sb.AppendLine($"  {code,3}  {meaning}");
        return sb.ToString();
    }

    public static string Version() => UpdateService.CurrentVersion.ToString(3);
}
