using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using IpScanner.Core.Localization;
using Microsoft.Data.Sqlite;

namespace IpScanner.Core;

/// <summary>The one place that turns an exception into a plain-language cause and next step
/// for user-facing failure dialogs. Unknown exception types fall back to the generic
/// dialog wording.</summary>
public static class FailureText
{
    /// <summary>Cause and next step for <paramref name="ex"/>. <paramref name="path"/> names the
    /// file or folder involved; when null (or blank) the file name is dropped from the sentence.</summary>
    public static (string Cause, string Next) Describe(Exception ex, string? path)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        switch (ex)
        {
            case UnauthorizedAccessException:
                return (hasPath ? string.Format(Loc.FailNoWriteFmt, path) : Loc.FailNoWrite, Loc.FailNoWriteNext);
            case InvalidDataException:
            case FormatException:
            case JsonException:
                return (hasPath ? string.Format(Loc.FailBadFormatFmt, path) : Loc.FailBadFormat, Loc.FailBadFormatNext);
            case SqliteException:
                return (Loc.FailDatabase, Loc.FailDatabaseNext);
            case UpdateSignatureException:
                return (Loc.FailUpdateSignature, Loc.FailUpdateSignatureNext);
            case CryptographicException:
                return (Loc.FailForeignKey, Loc.FailForeignKeyNext);
            case IOException:
                return (hasPath ? string.Format(Loc.FailFileBusyFmt, path) : Loc.FailFileBusy, Loc.FailFileBusyNext);
            default:
                return (Loc.ErrorDialogCause, Loc.ErrorDialogNextStep);
        }
    }
}
