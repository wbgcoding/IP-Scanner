using IpScanner.Core.Models;

namespace IpScanner.Core;

/// <summary>Decisions around the first-run card: on a fresh install nothing may leave the
/// PC (GitHub update check, internet latency pings) until the user has answered it.</summary>
internal static class FirstRunChoice
{
    /// <summary>The update check at launch. A fresh install waits for the card's answer.</summary>
    internal static bool StartUpdateCheckAtLaunch(bool freshInstall, ScanConfig config)
        => !freshInstall && config.CheckForUpdates;

    /// <summary>The internet latency loop is held until the card is answered.</summary>
    internal static bool HoldInternetPinging(bool freshInstall) => freshInstall;

    /// <summary>Stores the two ticked boxes in the config; the result says which of the two
    /// features to start now.</summary>
    internal static (bool CheckUpdates, bool InternetPing) Apply(
        ScanConfig config, bool checkUpdates, bool measureInternet)
    {
        config.CheckForUpdates = checkUpdates;
        config.EnableInternetPing = measureInternet;
        return (checkUpdates, measureInternet);
    }
}
