namespace IpScanner.Core.Scanner.Checks;

/// <summary>How much a check asks of the network. Safe checks only read what a service already
/// offers to every client. Discovery checks ask the service for inventory data it answers to
/// anyone who asks (shares, system text, names). Intrusive checks put traffic on the network that
/// reaches beyond the one target and stay off until the user confirms the warning.</summary>
public enum CheckCategory { Safe, Discovery, Intrusive }

/// <summary>Found: the check produced findings. NothingFound: the service answered but offered nothing
/// (or refused). NoAnswer: silence, a timeout or a connection that could not be made.</summary>
public enum CheckOutcome { Found, NothingFound, NoAnswer }

/// <summary>One fact a check found. <see cref="Key"/> is a stable id the UI turns into a label; the
/// value is text from the network, always sanitized and length-capped by the check.</summary>
public sealed record CheckFinding(string Key, string Value);

public sealed record CheckResult(string CheckId, CheckCategory Category, NetService Service,
    CheckOutcome Outcome, IReadOnlyList<CheckFinding> Findings)
{
    public static CheckResult Found(ICheck check, NetService service, IReadOnlyList<CheckFinding> findings)
        => new(check.Id, check.Category, service, findings.Count > 0 ? CheckOutcome.Found : CheckOutcome.NothingFound, findings);

    public static CheckResult Nothing(ICheck check, NetService service, params CheckFinding[] notes)
        => new(check.Id, check.Category, service, CheckOutcome.NothingFound, notes);

    public static CheckResult Silent(ICheck check, NetService service)
        => new(check.Id, check.Category, service, CheckOutcome.NoAnswer, Array.Empty<CheckFinding>());
}

/// <summary>A built-in check. There is no scripting and nothing downloadable: the catalog is code.</summary>
public interface ICheck
{
    string Id { get; }

    CheckCategory Category { get; }

    /// <summary>True for a check about the whole host or its network segment rather than one port:
    /// it runs once per host, for the first service it matches.</summary>
    bool PerHost { get; }

    /// <summary>Whether this service is one the check is meant for. The runner never calls
    /// <see cref="RunAsync"/> for a service this returns false for.</summary>
    bool Matches(NetService service);

    /// <summary>Runs against <paramref name="target"/> (an IPv4 address). Never throws except on cancel.</summary>
    Task<CheckResult> RunAsync(string target, NetService service, CancellationToken ct);
}

/// <summary>Which categories a run may use. Intrusive is only ever set after the user confirmed the warning.</summary>
public readonly record struct CheckSelection(bool Safe, bool Discovery, bool Intrusive)
{
    /// <summary>The default run: safe and discovery checks, never intrusive ones.</summary>
    public static CheckSelection Default => new(true, true, false);

    public bool Allows(CheckCategory category) => category switch
    {
        CheckCategory.Safe => Safe,
        CheckCategory.Discovery => Discovery,
        CheckCategory.Intrusive => Intrusive,
        _ => false,
    };
}

/// <summary>Picks and runs the checks for the open services of one host, one after the other.</summary>
public sealed class CheckRunner
{
    private readonly IReadOnlyList<ICheck> _checks;

    public CheckRunner(IReadOnlyList<ICheck> checks) => _checks = checks;

    /// <summary>The checks of the real catalog, talking to the network.</summary>
    public static CheckRunner Create() => new(CheckCatalog.Create(new NetworkCheckIo()));

    public IReadOnlyList<ICheck> Checks => _checks;

    /// <summary>The (check, service) pairs a run would execute: the service matches, the category is
    /// switched on, and a per-host check appears once.</summary>
    public IReadOnlyList<(ICheck Check, NetService Service)> Plan(IEnumerable<NetService> services, CheckSelection selection)
    {
        var list = services.ToList();
        var plan = new List<(ICheck, NetService)>();
        foreach (var check in _checks)
        {
            if (!selection.Allows(check.Category)) continue;
            foreach (var service in list)
            {
                if (!check.Matches(service)) continue;
                plan.Add((check, service));
                if (check.PerHost) break;
            }
        }
        return plan;
    }

    /// <param name="onResult">Called after each finished check, on the calling thread's context.</param>
    public async Task<IReadOnlyList<CheckResult>> RunAsync(string target, IEnumerable<NetService> services,
        CheckSelection selection, CancellationToken ct, Action<CheckResult>? onResult = null)
    {
        var results = new List<CheckResult>();
        foreach (var (check, service) in Plan(services, selection))
        {
            ct.ThrowIfCancellationRequested();
            CheckResult result;
            try { result = await check.RunAsync(target, service, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log.Debug(LogArea.Tools, "Check " + check.Id + " failed: " + ex.GetType().Name);
                result = CheckResult.Silent(check, service);
            }
            results.Add(result);
            onResult?.Invoke(result);
        }
        return results;
    }
}
