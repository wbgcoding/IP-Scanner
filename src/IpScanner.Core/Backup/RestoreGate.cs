namespace IpScanner.Core.Backup;

/// <summary>Keeps the rest of the app quiet while a restore prepares and swaps the files: no second restore,
/// no new scan, no repeat scan, and no config write still in flight when the files are replaced.
/// All members belong to the UI thread.</summary>
internal sealed class RestoreGate
{
    private Task _pendingSave = Task.CompletedTask;

    /// <summary>True from <see cref="TryEnter"/> until <see cref="Leave"/>.</summary>
    public bool Active { get; private set; }

    /// <summary>Starts a restore. Refused while a scan runs or another restore is under way.</summary>
    public bool TryEnter(bool scanning)
    {
        if (Active || scanning) return false;
        Active = true;
        return true;
    }

    public void Leave() => Active = false;

    /// <summary>The last moment before the swap: a scan may have started while the archive was staged.</summary>
    public bool CanCommit(bool scanning) => Active && !scanning;

    /// <summary>Registers a background config write so the restore can wait for it.</summary>
    public void Track(Task save) => _pendingSave = _pendingSave.IsCompleted ? save : Task.WhenAll(_pendingSave, save);

    /// <summary>Completes when every tracked config write is done, successful or not.</summary>
    public async Task DrainAsync()
    {
        try { await _pendingSave.ConfigureAwait(true); }
        catch { /* a failed save was already logged by its writer */ }
    }
}
