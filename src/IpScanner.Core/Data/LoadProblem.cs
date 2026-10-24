namespace IpScanner.Core.Data;

/// <summary>What went wrong while loading a persisted data file at startup; surfaced once
/// through the notice banner.</summary>
public enum LoadProblem
{
    None,
    /// <summary>The encryption key is missing or belongs to another Windows account.</summary>
    KeyUnavailable,
    /// <summary>The file could not be read and no backup was usable; it was moved aside.</summary>
    Unreadable,
    /// <summary>The file was damaged, moved aside, and the last backup was loaded instead.</summary>
    RestoredFromBackup,
    /// <summary>The file was written by a newer build; it is left in place and the store stays read-only.</summary>
    NewerFormat,
}
