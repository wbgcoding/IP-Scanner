using System;
using System.IO;
using System.Linq;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests;

public class FullPortScanSafetyGateTests
{
    [Fact]
    public void Confirm_AfterAcceptance_DoesNotPromptAgain()
    {
        var gate = new FullPortScanSafetyGate();
        int prompts = 0;

        Assert.True(gate.Confirm(false, () => { prompts++; return true; }));
        Assert.True(gate.Confirm(false, () => { prompts++; return false; }));
        Assert.Equal(1, prompts);
    }

    [Fact]
    public void Confirm_AfterCancellation_PromptsAgain()
    {
        var gate = new FullPortScanSafetyGate();
        int prompts = 0;

        Assert.False(gate.Confirm(false, () => { prompts++; return false; }));
        Assert.True(gate.Confirm(false, () => { prompts++; return true; }));
        Assert.Equal(2, prompts);
    }

    [Fact]
    public void Confirm_OutsideTarget_AsksEveryTime()
    {
        var gate = new FullPortScanSafetyGate();
        int prompts = 0;

        Assert.True(gate.Confirm(true, () => { prompts++; return true; }));
        Assert.True(gate.Confirm(true, () => { prompts++; return true; }));
        Assert.Equal(2, prompts);
    }

    [Fact]
    public void Confirm_OutsideAcceptance_DoesNotCountForInNetworkTargets()
    {
        var gate = new FullPortScanSafetyGate();
        int prompts = 0;

        Assert.True(gate.Confirm(true, () => { prompts++; return true; }));
        Assert.True(gate.Confirm(false, () => { prompts++; return true; }));
        Assert.True(gate.Confirm(false, () => { prompts++; return false; }));
        Assert.Equal(2, prompts);
    }

    [Fact]
    public void Shared_IsOneInstance() =>
        Assert.Same(FullPortScanSafetyGate.Shared, FullPortScanSafetyGate.Shared);

    [Fact]
    public void Configuration_HasNoFullPortScanWarningSuppressionKey()
    {
        var directory = Directory.CreateTempSubdirectory();
        string path = Path.Combine(directory.FullName, ScanConfig.ConfigFileName);
        try
        {
            ConfigManager.Save(path, new ScanConfig());
            string[] keys = File.ReadLines(path)
                .Where(line => line.Contains('='))
                .Select(line => line.Split('=', 2)[0].Trim())
                .ToArray();

            Assert.DoesNotContain(keys, key =>
                key.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("consent", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory.FullName);
        }
    }
}
