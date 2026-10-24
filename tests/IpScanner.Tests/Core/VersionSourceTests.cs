using System;
using System.IO;
using System.Linq;
using IpScanner.Core;
using Xunit;

namespace IpScanner.Tests.Core;

public class VersionSourceTests
{
    [Fact]
    public void CurrentVersion_EqualsTheGuiAssemblyVersion()
    {
        var gui = typeof(App).Assembly.GetName().Version!;
        Assert.Equal(gui.ToString(3), UpdateService.CurrentVersion.ToString(3));
    }

    [Fact]
    public void OnlyTheSharedPropsFileDefinesTheVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "IP-Scanner.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        Assert.Contains("<Version>", File.ReadAllText(Path.Combine(dir!.FullName, "Directory.Build.props")));
        foreach (var csproj in Directory.GetFiles(Path.Combine(dir.FullName, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(csproj);
            Assert.DoesNotContain("<Version>", text);
            Assert.DoesNotContain("<AssemblyVersion>", text);
            Assert.DoesNotContain("<FileVersion>", text);
        }
    }
}
