namespace IpScanner.Core;

/// <summary>Observes a task that nobody awaits so a failure is logged instead of lost.</summary>
internal static class BackgroundTask
{
    public static void Observe(Task task, string what)
        => Observe(task, what, text => Log.Error(LogArea.App, text));

    internal static void Observe(Task task, string what, Action<string> log)
    {
        _ = task.ContinueWith(
            t => log($"{what}: {t.Exception!.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
