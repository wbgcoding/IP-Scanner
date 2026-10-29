using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IpScanner.ViewModels.Tools;

/// <summary>Fills the target field of every target-taking tool page with the selected device. A field the
/// user typed into is left alone; a value this class filled earlier is replaced by a newer selection.</summary>
internal sealed class ToolsPrefill
{
    private readonly Dictionary<string, string> _applied = new();

    /// <summary>Fills the targets of the Wake-on-LAN (MAC), port, DNS, ping, route, certificate and MAC pages.</summary>
    public void Apply(ToolsViewModel vm, string? ip, string? mac)
    {
        ip = ip?.Trim();
        mac = mac?.Trim();
        if (!string.IsNullOrEmpty(ip))
        {
            Fill("ports", vm.PortCheck.Target, ip, v => vm.PortCheck.Target = v);
            Fill("dns", vm.DnsLookup.Target, ip, v => vm.DnsLookup.Target = v);
            Fill("ping", vm.PingRoute.Ping.Target, ip, v => vm.PingRoute.Ping.Target = v);
            Fill("route", vm.PingRoute.Route.Target, ip, v => vm.PingRoute.Route.Target = v);
            Fill("cert", vm.Certificate.Target, ip, v => vm.Certificate.Target = v);
            Fill("arp", vm.Mac.ArpIp, ip, v => vm.Mac.ArpIp = v);
        }
        if (!string.IsNullOrEmpty(mac))
        {
            if (Fill("wol", vm.WakeOnLan.Mac, mac, v => vm.WakeOnLan.Mac = v) && !string.IsNullOrEmpty(ip))
                vm.WakeOnLan.TargetIp = ip;   // the page polls this address after the magic packet
            Fill("vendor", vm.Mac.VendorMac, mac, v => vm.Mac.VendorMac = v);
        }
    }

    private bool Fill(string key, string current, string value, Action<string> set)
    {
        bool typed = current.Trim().Length > 0 && !(_applied.TryGetValue(key, out var last) && last == current);
        if (typed) return false;
        set(value);
        _applied[key] = value;
        return true;
    }

    /// <summary>Points the continuous ping at <paramref name="ip"/> and starts it, ending a run on another target first.</summary>
    public static async Task StartPingAsync(PingToolViewModel ping, string ip)
    {
        if (ping.IsRunning) { ping.Stop(); await WaitUntilIdleAsync(() => ping.IsRunning); }
        ping.Target = ip;
        await ping.StartAsync();
    }

    /// <summary>Points the route trace at <paramref name="ip"/> and starts it, ending a run on another target first.</summary>
    public static async Task StartRouteAsync(RouteToolViewModel route, string ip)
    {
        if (route.IsRunning) { route.Stop(); await WaitUntilIdleAsync(() => route.IsRunning); }
        route.Target = ip;
        await route.StartAsync();
    }

    private static async Task WaitUntilIdleAsync(Func<bool> running)
    {
        for (int i = 0; i < 150 && running(); i++) await Task.Delay(20);
    }
}
