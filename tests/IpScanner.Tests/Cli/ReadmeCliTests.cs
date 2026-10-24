using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IpScanner.Core.Cli;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using IpScanner.Core.Scanner.Checks;
using IpScanner.Core.Scanner.Os;
using Xunit;

namespace IpScanner.Tests.Cli;

/// <summary>The README command line section is generated knowledge: its flag table must match the flag
/// table of the program, and every example in it must run and print what the README shows.</summary>
[Collection("Localization")]
public sealed class ReadmeCliTests
{
    private const string Program = "IP-Scanner.exe ";

    private static string ReadmePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("README.md not found above the test binaries");
    }

    private static string CommandLineSection()
    {
        var lines = File.ReadAllLines(ReadmePath());
        var start = Array.FindIndex(lines, l => l.StartsWith("## Command line", StringComparison.Ordinal));
        Assert.True(start >= 0, "README has no 'Command line' section");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (end < 0) end = lines.Length;
        return string.Join('\n', lines[start..end]);
    }

    private static string Norm(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    private static string Unescape(string cell) => cell.Replace("\\|", "|").Trim().Trim('`');

    private static IEnumerable<(string Display, string Help)> TableRows(string section)
    {
        foreach (var line in section.Split('\n'))
        {
            if (!line.StartsWith("| `", StringComparison.Ordinal)) continue;
            var cells = Regex.Split(line.Trim().TrimStart('|').TrimEnd('|'), @"(?<!\\)\|");
            if (cells.Length < 2) continue;
            yield return (Unescape(cells[0]), cells[1].Replace("\\|", "|").Trim());
        }
    }

    /// <summary>Command block followed by an excerpt block, in README order.</summary>
    private static List<(string Command, List<string> Excerpt)> ReadmeExamples()
    {
        var section = CommandLineSection().Split('\n');
        var result = new List<(string, List<string>)>();
        for (var i = 0; i < section.Length; i++)
        {
            if (section[i].Trim() != "```" || i + 1 >= section.Length) continue;
            var command = section[i + 1].Trim();
            if (!command.StartsWith(Program, StringComparison.Ordinal)) continue;
            var j = i + 2;
            while (j < section.Length && section[j].Trim() != "```text") j++;
            var excerpt = new List<string>();
            for (j++; j < section.Length && section[j].Trim() != "```"; j++)
                if (section[j].Trim().Length > 0) excerpt.Add(section[j]);
            result.Add((command, excerpt));
            i = j;
        }
        return result;
    }

    public static CliServices Fake() => new()
    {
        Ping = (ip, _) => new PingResult(ip is "192.0.2.1" or "192.0.2.5" or "192.0.2.10" or "2001:db8::10", 1.0, 64),
        PortRunner = (ip, spec, _, _, _, _) =>
        {
            var list = new List<PortResult>();
            foreach (var p in spec.Tcp)
            {
                var open = p is 22 or 80 or 443;
                var service = p switch
                {
                    22 => new NetService("ssh", 22, NetProtocol.Tcp, "OpenSSH 9.6"),
                    80 => new NetService("http", 80, NetProtocol.Tcp, "nginx 1.24"),
                    _ => null,
                };
                list.Add(new PortResult(p, NetProtocol.Tcp, open ? PortState.Open : PortState.Closed,
                    open ? PortReason.SynAck : PortReason.Rst, open ? service : null));
            }
            foreach (var p in spec.Udp)
            {
                var open = p is 53 or 123;
                list.Add(new PortResult(p, NetProtocol.Udp, open ? PortState.Open : PortState.Closed,
                    open ? PortReason.UdpResponse : PortReason.Rst, null));
            }
            return Task.FromResult<IReadOnlyList<PortResult>>(list);
        },
        CheckRunner = (ip, services, _, _) =>
        {
            var http = services.FirstOrDefault(s => s.Port == 80) ?? new NetService("http", 80, NetProtocol.Tcp);
            IReadOnlyList<CheckResult> r =
            [
                new CheckResult("http-title", CheckCategory.Safe, http, CheckOutcome.Found,
                    [new CheckFinding("title", "Welcome")]),
            ];
            return Task.FromResult(r);
        },
        OsRunner = (_, _, _) => Task.FromResult<OsEstimate?>(new OsEstimate(OsFamily.UnixLike, 85)),
        Network = _ => new NetworkInfo { Ip = "192.0.2.5", SubnetMask = "255.255.255.0", Gateway = "192.0.2.1", Interface = "Test" },
        Cards = () => [],
        Now = () => new DateTime(2026, 1, 2, 3, 4, 5),
        Availability = _ => (true, null),
        ReadLines = _ => new[] { "192.0.2.2", "192.0.2.10" },
    };

    /// <summary>Runs one README command line; output files go into <paramref name="dir"/>.</summary>
    public static (int Code, string Out, string Err, List<string> Files) RunExample(string command, string dir)
    {
        var args = command[Program.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var files = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0 && args[i - 1] is "-oJ" or "-oX" or "-oG" or "-oN" or "-oC" or "-oH" or "-oM" && args[i] != "-")
            {
                args[i] = Path.Combine(dir, args[i]);
                files.Add(args[i]);
            }
        }
        var so = new StringWriter();
        var se = new StringWriter();
        var code = CliApp.Run(args, so, se, CancellationToken.None, Fake(), "IP-Scanner");
        return (code, so.ToString(), se.ToString(), files);
    }

    [Fact]
    public void FlagTable_MatchesTheFlagsOfTheProgram()
    {
        var rows = TableRows(CommandLineSection()).ToList();
        foreach (var flag in CliFlags.All)
        {
            var row = rows.FirstOrDefault(r => Norm(r.Display) == Norm(flag.Display));
            Assert.True(row != default, $"README table has no row for {flag.Display}");
            Assert.Equal(Norm(flag.Help), Norm(row.Help));
        }
        Assert.Equal(CliFlags.All.Count, rows.Count);
    }

    [Fact]
    public void Examples_AreTheOnesOfTheHelp()
    {
        var readme = ReadmeExamples().Select(e => e.Command).ToList();
        var help = CliHelp.Examples.Select(e => Program + e.Args).ToList();
        Assert.Equal(help, readme);
    }

    [Fact]
    public void Examples_RunAndPrintTheDocumentedShape()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ipscan-readme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var examples = ReadmeExamples();
            Assert.True(examples.Count >= 12, "README documents fewer than twelve examples");
            foreach (var (command, excerpt) in examples)
            {
                var (code, output, err, files) = RunExample(command, dir);
                Assert.True(code == CliExit.Ok, $"{command} exited with {code}: {err}");
                Assert.NotEmpty(excerpt);
                var normalized = output.Split('\n').Select(Norm).ToList();
                foreach (var line in excerpt)
                {
                    var wanted = Norm(line.TrimEnd().EndsWith(" ...", StringComparison.Ordinal) ? line.TrimEnd()[..^4] : line);
                    Assert.True(normalized.Any(l => l.Contains(wanted, StringComparison.Ordinal)),
                        $"{command}: README shows '{line.Trim()}' but the output does not contain it");
                }
                foreach (var f in files)
                    Assert.True(File.Exists(f) && new FileInfo(f).Length > 0, $"{command}: {Path.GetFileName(f)} was not written");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ReadmeStatesPurposeAndLegalNote()
    {
        var section = Norm(CommandLineSection());
        Assert.Contains(Norm(CliHelp.LegalNote()), section);
        Assert.Contains(Norm(CliHelp.Purpose()), section);
    }

    [Fact]
    public void Help_ListsEveryFlagAndTheHelpModelCoversThem()
    {
        var help = CliHelp.Text("IP-Scanner");
        var model = HelpDoc.Options().SelectMany(s => s.Rows).ToList();
        foreach (var flag in CliFlags.All)
        {
            Assert.Contains(flag.Names[0], help);
            Assert.Contains(model, r => Norm(r.Option) == Norm(flag.Display));
        }
    }
}
