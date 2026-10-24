using IpScanner.Core.Export;
using IpScanner.Core.Models;
using System.IO;
using Xunit;

namespace IpScanner.Tests.Core;

public class ExportRequestTests
{
    [Theory]
    [InlineData(ExportScope.All, "one,two,three")]
    [InlineData(ExportScope.Filtered, "three,one")]
    [InlineData(ExportScope.Selected, "two")]
    public void Rows_UsesTheChosenSnapshotAndPreservesItsOrder(ExportScope scope, string expected)
    {
        var request = new ExportRequest(scope, ExportFormat.Csv, ExportTarget.File);
        var result = request.Rows(new[] { "one", "two", "three" },
            new[] { "three", "one" }, new[] { "two" });
        Assert.Equal(expected, string.Join(',', result));
    }

    [Fact]
    public void FilteredCsv_ContainsOnlyRowsInTheCurrentView()
    {
        var one = new Device("192.0.2.1");
        var two = new Device("192.0.2.2");
        one.RecordPing(new PingResult(true, 1, 64));
        two.RecordPing(new PingResult(true, 1, 64));
        var request = new ExportRequest(ExportScope.Filtered, ExportFormat.Csv, ExportTarget.File);
        var rows = request.Rows(new[] { one, two }, new[] { two }, new[] { one });
        var dir = Directory.CreateTempSubdirectory().FullName;
        var csv = File.ReadAllText(CsvExporter.Write(rows, dir, "20260919_000000", ""));
        Assert.Contains("192.0.2.2", csv);
        Assert.DoesNotContain("192.0.2.1", csv);
    }
}
