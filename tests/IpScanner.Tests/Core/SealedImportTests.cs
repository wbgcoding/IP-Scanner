using System;
using System.Collections.Generic;
using System.IO;
using IpScanner.Core.Data;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class SealedImportTests
{
    private static string Write(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "ip-scanner-test-" + Guid.NewGuid().ToString("N") + ".conf");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ImportWithoutTheKeys_KeepsTheCurrentCommunityAndTargets()
    {
        var path = Write("# snmp_community  not set here\nping_count = 4\n");
        try
        {
            var current = new ScanConfig { SnmpCommunity = "private1", RecentWakeTargets = new List<string> { "10.0.0.5" } };
            var imported = ConfigManager.Load(path);
            Assert.Equal("public", imported.SnmpCommunity);
            SealedImport.KeepCurrentUnlessImported(imported, current, path);
            Assert.Equal("private1", imported.SnmpCommunity);
            Assert.Equal(new[] { "10.0.0.5" }, imported.RecentWakeTargets);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImportThatCarriesTheKeys_WinsOverTheCurrentValues()
    {
        var path = Write("snmp_community = other\nrecent_wake_targets = 10.0.0.9\n");
        try
        {
            var current = new ScanConfig { SnmpCommunity = "private1", RecentWakeTargets = new List<string> { "10.0.0.5" } };
            var imported = ConfigManager.Load(path);
            SealedImport.KeepCurrentUnlessImported(imported, current, path);
            Assert.Equal("other", imported.SnmpCommunity);
            Assert.Equal(new[] { "10.0.0.9" }, imported.RecentWakeTargets);
        }
        finally { File.Delete(path); }
    }
}
