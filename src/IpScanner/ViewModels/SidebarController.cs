using IpScanner.Core.Localization;
using IpScanner.Core.Models;

namespace IpScanner.ViewModels;

/// <summary>The three sidebar lists, each a plain list of <see cref="NetEntry"/> lines on the config.</summary>
public enum EntryKind { Network, Pinned, Host }

/// <summary>What a save attempt came to; anything but <see cref="None"/> means nothing was saved.</summary>
public enum EntryError { None, Invalid, Duplicate, PrefixTooLarge }

/// <param name="Error">Why the save was refused, or <see cref="EntryError.None"/>.</param>
/// <param name="Target">The target the message talks about (the trimmed input).</param>
/// <param name="DuplicateName">Another entry already uses this name; a hint, never a blocker.</param>
public readonly record struct EntrySaveResult(EntryError Error, string Target, bool DuplicateName)
{
    public bool Saved => Error == EntryError.None;
}

/// <summary>What the edit popup shows for one row, or for a fresh entry.</summary>
public sealed record EntryEditSpec(EntryKind Kind, string Title, string TargetLabel, string Target,
    string Name, string Color, string? SourceKey, bool TargetReadOnly, bool CanDelete);

/// <summary>What the controller needs from the window: everything it does to the outside world.</summary>
public sealed class SidebarHooks
{
    /// <summary>The list changed: hand the config to the view model and re-apply dependent settings.</summary>
    public Action ConfigChanged { get; init; } = () => { };
    public Action RefreshNetworks { get; init; } = () => { };
    public Action RefreshPinned { get; init; } = () => { };
    public Action RestartInternetHosts { get; init; } = () => { };
    public Action Persist { get; init; } = () => { };
    /// <summary>A delete went through: show the undo hint for the removed entry.</summary>
    public Action<EntryKind, NetEntry> EntryRemoved { get; init; } = (_, _) => { };
}

/// <summary>Add, edit, delete, arm and undo for the three sidebar lists, with no window in sight.
/// The window's click handlers only translate a click into one call here.</summary>
public sealed class SidebarController
{
    private readonly Func<ScanConfig> _config;
    private readonly SidebarHooks _hooks;
    private readonly ArmThenConfirm _arm;

    public SidebarController(Func<ScanConfig> config, SidebarHooks hooks, ArmThenConfirm? arm = null)
    {
        _config = config;
        _hooks = hooks;
        _arm = arm ?? new ArmThenConfirm();
    }

    // ── Edit popup ──

    public EntryEditSpec? EditSpecFor(object? row) => row switch
    {
        NetworkInfoViewModel net => new(EntryKind.Network, Loc.Network, Loc.Subnets, net.Cidr, net.RawName,
            net.RawColor, net.SourceTarget, TargetReadOnly: net.IsPrimary, CanDelete: net.CanDelete),
        PinnedIpViewModel p => new(EntryKind.Pinned, Loc.PinnedIps, Loc.IpOrHostname, p.Ip, p.Name, p.Color,
            p.Ip, TargetReadOnly: false, CanDelete: true),
        InternetHostViewModel h => new(EntryKind.Host, Loc.InternetLatency, Loc.IpAddress, h.Ip, h.Name, "",
            h.Ip, TargetReadOnly: false, CanDelete: true),
        _ => null,
    };

    public EntryEditSpec AddSpecFor(EntryKind kind) => kind switch
    {
        EntryKind.Network => new(kind, Loc.Network, Loc.Subnets, "", "", "", null, false, false),
        EntryKind.Pinned => new(kind, Loc.PinnedIps, Loc.IpOrHostname, "", "", "", null, false, false),
        _ => new(kind, Loc.InternetLatency, Loc.IpAddress, "", "", "", null, false, false),
    };

    /// <summary>Validates and stores one entry. A read-only target (the primary network) keeps its own
    /// key; an address another entry already uses is refused unless it is this entry's own address.</summary>
    public EntrySaveResult Save(EntryKind kind, string? sourceKey, bool targetReadOnly,
        string targetInput, string nameInput, string color)
    {
        var name = nameInput.Trim();
        string target;
        if (targetReadOnly)
        {
            target = sourceKey ?? targetInput.Trim();
        }
        else
        {
            target = targetInput.Trim();
            // A pinned entry may also be a hostname, resolved fresh at every scan start.
            bool valid = kind switch
            {
                EntryKind.Network => Core.Net.AddressSyntax.IsValidSubnetEntry(target) && !IsHugeIpv6Prefix(target),
                EntryKind.Pinned => Core.Net.AddressSyntax.IsValidAddress(target) || NetEntry.IsValidHostname(target),
                _ => Core.Net.AddressSyntax.IsValidAddress(target),
            };
            if (!valid && kind == EntryKind.Network && IsHugeIpv6Prefix(target))
                return new(EntryError.PrefixTooLarge, target, false);
            if (!valid) return new(EntryError.Invalid, target, false);
            target = Core.Net.AddressSyntax.Normalize(target);
        }
        // An address already claimed by a different entry would silently overwrite it. Re-naming or
        // re-coloring the SAME entry without changing its address stays allowed (the sourceKey exception).
        if (target != sourceKey && HasDuplicateTarget(kind, target))
            return new(EntryError.Duplicate, target, false);
        // Names are free text, not a key: warn, never block.
        bool dupName = NetEntry.HasDuplicateName(ListFor(kind), name, sourceKey);
        Store(kind, sourceKey, target, name, color, skipIfBlank: kind == EntryKind.Network && targetReadOnly);
        return new(EntryError.None, target, dupName);
    }

    private static bool IsHugeIpv6Prefix(string target)
        => Core.Net.Ipv6Prefix.TryParse(target, out var p) && !Core.Scanner.Ipv6.Ipv6Discovery.CanSweep(p);

    public void Delete(EntryKind kind, string sourceKey)
    {
        var list = ListFor(kind).Select(NetEntry.Parse).ToList();
        var removed = list.FirstOrDefault(en => en.Target == sourceKey);
        SetList(kind, list.Where(en => en.Target != sourceKey).Select(en => en.ToString()).ToList());
        _hooks.ConfigChanged();
        // The refresh can rebuild the lists, which disarms and hides the notice; the undo hint has to
        // go up after that settles, or this very delete would hide it again.
        Refresh(kind);
        if (removed is not null) _hooks.EntryRemoved(kind, removed);
        _hooks.Persist();
    }

    /// <summary>Undo: puts a removed entry back. A deletable network is never the primary one, so the
    /// restore is always a plain save.</summary>
    public void Restore(EntryKind kind, NetEntry entry)
        => Store(kind, null, entry.Target, entry.Name, kind == EntryKind.Host ? "" : entry.Color, skipIfBlank: false);

    /// <summary>Pins an address (or hostname) without a name or color, as the IP column does.</summary>
    public void Pin(string target) => Store(EntryKind.Pinned, null, target, "", "", skipIfBlank: false);

    /// <summary>Replaces a whole list at once (list import).</summary>
    public void ReplaceAll(EntryKind kind, List<string> raw)
    {
        SetList(kind, raw);
        _hooks.ConfigChanged();
        Refresh(kind);
        _hooks.Persist();
    }

    public List<string> ListFor(EntryKind kind) => kind switch
    {
        EntryKind.Network => _config().Subnets,
        EntryKind.Pinned => _config().PinnedIps,
        EntryKind.Host => _config().InternetHosts,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public bool HasDuplicateTarget(EntryKind kind, string target)
        => ListFor(kind).Select(NetEntry.Parse).Any(en => en.Target == target);

    // ── Per-row delete gesture ──

    /// <summary>The row's delete chip or Delete key: the first call arms the row, the second within the
    /// timeout removes it. Returns false when the row cannot be deleted here (unknown row, primary network).</summary>
    public bool DeleteRow(object? row)
    {
        switch (row)
        {
            case NetworkInfoViewModel { CanDelete: true, SourceTarget: { } key } net:
                if (_arm.Confirm(net)) Delete(EntryKind.Network, key);
                return true;
            case PinnedIpViewModel p:
                if (_arm.Confirm(p)) Delete(EntryKind.Pinned, p.Ip);
                return true;
            case InternetHostViewModel h:
                if (_arm.Confirm(h)) Delete(EntryKind.Host, h.Ip);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Drops an armed row that a list rebuild may have replaced.</summary>
    public void Disarm() => _arm.Disarm();

    // ── Shared shape ──

    // All three lists are NetEntry lines re-parsed, re-targeted and re-serialized the same way; only
    // the storage slot and the refresh differ.
    private void Store(EntryKind kind, string? sourceKey, string target, string name, string color, bool skipIfBlank)
    {
        var entries = ListFor(kind).Select(NetEntry.Parse).ToList();
        if (sourceKey is not null) entries.RemoveAll(en => en.Target == sourceKey);
        entries.RemoveAll(en => en.Target == target);   // re-targeting an existing one replaces it
        if (!skipIfBlank || name.Length > 0 || color.Length > 0)
            entries.Add(new NetEntry(target, name, kind == EntryKind.Host ? "" : color));
        SetList(kind, entries.Select(en => en.ToString()).ToList());
        _hooks.ConfigChanged();
        Refresh(kind);
        _hooks.Persist();
    }

    private void SetList(EntryKind kind, List<string> value)
    {
        var cfg = _config();
        switch (kind)
        {
            case EntryKind.Network: cfg.Subnets = value; break;
            case EntryKind.Pinned: cfg.PinnedIps = value; break;
            case EntryKind.Host: cfg.InternetHosts = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Re-applies one list to the live view (rows, names or the internet ping loop).</summary>
    public void Refresh(EntryKind kind)
    {
        switch (kind)
        {
            case EntryKind.Network: _hooks.RefreshNetworks(); break;
            case EntryKind.Pinned: _hooks.RefreshPinned(); break;
            default: _hooks.RestartInternetHosts(); break;
        }
    }
}
