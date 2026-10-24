using System;
using System.IO;
using System.Text;
using Xunit;

namespace IpScanner.Tests;

[Collection("LogFile")]
public class AppTests
{
    // Unobserved background-task exceptions now go through this same
    // sink instead of being silently marked observed and dropped.
    [Fact]
    public void AppendErrorLog_WritesTimestampedEntry()
    {
        var original = File.Exists(App.ErrorLogPath) ? File.ReadAllText(App.ErrorLogPath) : null;
        try
        {
            var marker = $"test-marker-{Guid.NewGuid()}";
            App.AppendErrorLog(marker);

            var content = File.ReadAllText(App.ErrorLogPath);
            Assert.Contains(marker, content);
            Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\]", content);
        }
        finally
        {
            if (original is null) File.Delete(App.ErrorLogPath);
            else File.WriteAllText(App.ErrorLogPath, original, new UTF8Encoding(false));
        }
    }

    [Fact]
    public void AppendErrorLog_OverCapacity_RotatesInsteadOfGrowingForever()
    {
        var original = File.Exists(App.ErrorLogPath) ? File.ReadAllText(App.ErrorLogPath) : null;
        try
        {
            File.WriteAllText(App.ErrorLogPath, new string('x', 6 * 1024 * 1024));
            App.AppendErrorLog("after-rotation");

            var content = File.ReadAllText(App.ErrorLogPath);
            Assert.DoesNotContain('x'.ToString(), content);
            Assert.Contains("after-rotation", content);
        }
        finally
        {
            File.Delete(App.ErrorLogPath + ".1");   // the rotated generation
            if (original is null) File.Delete(App.ErrorLogPath);
            else File.WriteAllText(App.ErrorLogPath, original, new UTF8Encoding(false));
        }
    }
}
