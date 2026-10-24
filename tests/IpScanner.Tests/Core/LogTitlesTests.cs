using IpScanner.Core;
using IpScanner.Core.Localization;
using Xunit;

namespace IpScanner.Tests.Core;

public class LogTitlesTests
{
    // The text follows the UI language, so expectations are looked up, not spelled out.
    private static string Title(string property) =>
        (string)typeof(Loc).GetProperty(property)!.GetValue(null)!;

    [Theory]
    [InlineData("SQLite Error 5: 'database is locked'.", "LogTitleDbLocked")]
    [InlineData("Microsoft.Data.Sqlite.SqliteException: SQLite Error 8: 'attempt to write a readonly database'.", "LogTitleDbReadOnly")]
    [InlineData("SQLite Error 10: 'disk I/O error'.", "LogTitleDiskIo")]
    [InlineData("System.IO.IOException: There is not enough space on the disk.", "LogTitleDiskFull")]
    [InlineData("System.Runtime.InteropServices.COMException (0x800401D0): CLIPBRD_E_CANT_OPEN", "LogTitleClipboardBusy")]
    [InlineData("The process cannot access the file 'a.csv' because it is being used by another process.", "LogTitleFileInUse")]
    [InlineData("System.Net.Sockets.SocketException (10065): A socket operation was attempted to an unreachable host.", "LogTitleNoRoute")]
    [InlineData("System.Net.Sockets.SocketException: No route to host", "LogTitleNoRoute")]
    [InlineData("System.Net.Sockets.SocketException (11001): No such host is known.", "LogTitleNameNotFound")]
    [InlineData("No connection could be made because the target machine actively refused it.", "LogTitleRefused")]
    [InlineData("The operation has timed out.", "LogTitleTimedOut")]
    [InlineData("System.UnauthorizedAccessException: Access to the path 'x.txt' is denied.", "LogTitleAccessDenied")]
    [InlineData("Port collection failed", "LogTitlePortCollection")]
    [InlineData("Update signature check failed", "LogTitleUpdateSignature")]
    [InlineData("Startup scan failed", "LogTitleStartupScan")]
    public void KnownErrorText_GetsAReadableTitle(string raw, string property) =>
        Assert.Equal(Title(property), LogTitles.For(raw, null));

    [Fact]
    public void TitleIsFound_InTheExceptionDetail_NotOnlyInTheMessage() =>
        Assert.Equal(Title("LogTitleDbLocked"),
            LogTitles.For("Scan archive write failed", "SqliteException: SQLite Error 5: 'database is locked'."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Something unexpected happened")]
    public void UnknownOrEmptyText_HasNoTitle(string? raw) =>
        Assert.Null(LogTitles.For(raw, null));

    [Fact]
    public void ANamedStep_YieldsToARecognisedCause() =>
        Assert.Equal(Title("LogTitleTimedOut"), LogTitles.For("Port collection failed", "The operation has timed out."));

    [Fact]
    public void LockedDatabaseWinsOverTheGenericAccessRule() =>
        Assert.Equal(Title("LogTitleDbLocked"),
            LogTitles.For("database is locked", "administrator note"));
}
