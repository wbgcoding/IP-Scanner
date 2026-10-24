using System.Linq;
using IpScanner.Core.Localization;

namespace IpScanner;

/// <summary>The "automatic + every language" option lists behind the language and the
/// date format boxes, kept in one place so both boxes always offer the same entries.</summary>
internal static class ModeLists
{
    public static string[] LanguageCodes()
        => new[] { "auto" }.Concat(Loc.Languages.Select(l => l.Code)).ToArray();

    public static string[] LanguageLabels(bool nameDetected = false)
        => new[] { nameDetected ? Loc.LangAutoDetected : Loc.LangAuto }.Concat(Loc.Languages.Select(l => l.Name)).ToArray();
}
