using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace IpScanner.Core.Export;

/// <summary>The four renderings every result model has. Text follows the UI language; the other three use
/// fixed English identifiers so a script can rely on them.</summary>
public enum ReportFormat { Text, Xml, Json, Grepable }

public static class ReportFormats
{
    public static IReadOnlyList<ReportFormat> All { get; } = Enum.GetValues<ReportFormat>();

    public static string Name(ReportFormat f) => f switch
    {
        ReportFormat.Text => "Text",
        ReportFormat.Xml => "XML",
        ReportFormat.Json => "JSON",
        _ => "Grepable"
    };

    /// <summary>File extension for a scan report file.</summary>
    public static string Extension(ReportFormat f) => f switch
    {
        ReportFormat.Text => "txt",
        ReportFormat.Xml => "xml",
        ReportFormat.Json => "json",
        _ => "gnmap"
    };

    /// <summary>Text-based files are saved as UTF-8; only the text report carries a byte order mark.</summary>
    public static Encoding Encoding(ReportFormat f) => f == ReportFormat.Text ? System.Text.Encoding.UTF8 : new UTF8Encoding(false);
}

/// <summary>Low-level helpers shared by every report writer (scan report and scan comparison).</summary>
internal static class ReportCodec
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Iso(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    public static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Text read off the network may carry characters XML cannot hold.</summary>
    public static string XmlSafe(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i + 1 < s.Length && XmlConvert.IsXmlSurrogatePair(s[i + 1], c))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"-" for a missing value; tabs and line breaks would break the one-line layout.</summary>
    public static string Field(string? s) =>
        string.IsNullOrEmpty(s) ? "-" : new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    /// <summary>One-line text for the terminal: control characters (including ESC) become spaces.</summary>
    public static string Line(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    /// <summary>Multi-line text for the terminal: line breaks stay, other control characters become spaces.</summary>
    public static string Block(string? s) =>
        string.IsNullOrEmpty(s) ? "" : new string(s.Select(c => char.IsControl(c) && c != '\n' ? ' ' : c).ToArray());

    /// <summary>Runs <paramref name="body"/> on an indented XML writer and returns the document text.
    /// Line breaks inside values are kept as character references so they survive a round trip.</summary>
    public static string Xml(Action<XmlWriter> body)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Entitize
        };
        using (var w = XmlWriter.Create(new Utf8StringWriter(sb), settings))
        {
            body(w);
        }
        return sb.ToString();
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        // XmlWriter writes the declaration from the writer's encoding; the text is saved as UTF-8 either way.
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
