namespace IpScanner.Core.Export;

public enum ExportScope { All, Filtered, Selected }
public enum ExportFormat { Txt, Csv, Html, Json, Xml, Grepable }
public enum ExportTarget { File, ClipboardText, ClipboardTable }

public readonly record struct ExportRequest(ExportScope Scope, ExportFormat Format, ExportTarget Target)
{
    public IReadOnlyList<T> Rows<T>(IEnumerable<T> all, IEnumerable<T> filtered, IEnumerable<T> selected) =>
        (Scope switch
        {
            ExportScope.All => all,
            ExportScope.Filtered => filtered,
            ExportScope.Selected => selected,
            _ => throw new ArgumentOutOfRangeException(nameof(Scope))
        }).ToList();
}
