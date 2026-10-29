using System.IO;
using System.Linq;
using IpScanner.Core;
using IpScanner.Core.Data;
using Xunit;

namespace IpScanner.Tests.Core;

[Collection("LogFile")]
public class LocalCryptoLogTests
{
    [Fact]
    public void UnusableKeyFile_IsReportedOncePerPath_NotOnEveryLoad()
    {
        var keyPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "scanner.key");
        File.WriteAllBytes(keyPath, [1, 2, 3, 4, 5, 6, 7, 8]);   // not a DPAPI blob
        Log.ClearBuffer();

        for (var i = 0; i < 4; i++)
        {
            Assert.Null(LocalCrypto.TryLoadExisting(keyPath));
            Assert.Null(LocalCrypto.TryLoad(keyPath));
        }

        Assert.Single(Log.Snapshot(), e => e.Area == LogArea.Config && e.Message == "Decrypting stored data failed");
    }
}
