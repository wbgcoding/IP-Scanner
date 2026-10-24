using System.IO;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class WpcapLoadTests
{
    [Fact]
    public void OnlyAnExistingFileDirectlyInTheDriverFolderIsTrusted()
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), ".tmp-wpcap-" + System.Guid.NewGuid().ToString("N"));
        var driver = Path.Combine(root, "Npcap");
        Directory.CreateDirectory(Path.Combine(driver, "sub"));
        try
        {
            var inside = Path.Combine(driver, "wpcap.dll");
            File.WriteAllText(inside, "x");
            File.WriteAllText(Path.Combine(root, "wpcap.dll"), "x");
            File.WriteAllText(Path.Combine(driver, "sub", "wpcap.dll"), "x");

            Assert.True(Wpcap.IsTrustedDriverFile(inside, driver));
            Assert.True(Wpcap.IsTrustedDriverFile(inside, driver + Path.DirectorySeparatorChar));
            Assert.False(Wpcap.IsTrustedDriverFile(Path.Combine(driver, "missing.dll"), driver));
            Assert.False(Wpcap.IsTrustedDriverFile(Path.Combine(driver, "sub", "wpcap.dll"), driver));
            Assert.False(Wpcap.IsTrustedDriverFile(Path.Combine(driver, "..", "wpcap.dll"), driver));
            Assert.False(Wpcap.IsTrustedDriverFile("wpcap.dll", driver));
            Assert.False(Wpcap.IsTrustedDriverFile(inside + "\0", driver));
        }
        finally { Directory.Delete(root, true); }
    }
}
