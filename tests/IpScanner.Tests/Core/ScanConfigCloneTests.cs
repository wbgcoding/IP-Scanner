using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IpScanner.Core.Models;
using Xunit;

namespace IpScanner.Tests.Core;

public class ScanConfigCloneTests
{
    private static PropertyInfo[] SettableProps() => typeof(ScanConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetSetMethod() is not null)
        .ToArray();

    /// <summary>A config where every property holds a value that differs from its default.</summary>
    private static ScanConfig NonDefault()
    {
        var cfg = new ScanConfig();
        foreach (var p in SettableProps())
        {
            object v = p.PropertyType switch
            {
                var t when t == typeof(string) => "x-" + p.Name,
                var t when t == typeof(bool) => !(bool)p.GetValue(cfg)!,
                var t when t == typeof(int) => (int)p.GetValue(cfg)! + 7,
                var t when t == typeof(double) => (double)p.GetValue(cfg)! + 1.5,
                var t when t == typeof(List<string>) => new List<string> { "a-" + p.Name, "b-" + p.Name },
                _ => throw new NotSupportedException($"ScanConfig.{p.Name} has type {p.PropertyType}: teach this test and Clone about it"),
            };
            p.SetValue(cfg, v);
        }
        return cfg;
    }

    private static void AssertEqual(PropertyInfo p, object? expected, object? actual)
    {
        if (expected is List<string> e)
            Assert.True(e.SequenceEqual((List<string>)actual!), $"{p.Name} differs");
        else
            Assert.True(Equals(expected, actual), $"{p.Name} differs: {expected} vs {actual}");
    }

    [Fact]
    public void Clone_CopiesEveryPublicProperty()
    {
        var src = NonDefault();
        var copy = src.Clone();
        foreach (var p in SettableProps())
        {
            AssertEqual(p, p.GetValue(src), p.GetValue(copy));
            if (p.PropertyType == typeof(List<string>))
                Assert.False(ReferenceEquals(p.GetValue(src), p.GetValue(copy)), $"{p.Name} list is shared");
        }
    }

    [Fact]
    public void Clone_ListEditsDoNotLeakIntoTheSource()
    {
        var src = new ScanConfig { Subnets = { "10.0.0.0/24" } };
        var copy = src.Clone();
        copy.Subnets.Add("10.0.1.0/24");
        Assert.Single(src.Subnets);
    }

    [Fact]
    public void CloneWith_CopiesEveryPropertyExceptPingCount()
    {
        var src = NonDefault();
        var copy = src.CloneWith(src.PingCount + 1);
        Assert.Equal(src.PingCount + 1, copy.PingCount);
        foreach (var p in SettableProps().Where(p => p.Name != nameof(ScanConfig.PingCount)))
            AssertEqual(p, p.GetValue(src), p.GetValue(copy));
    }

    [Fact]
    public void ResetKeepingLists_KeepsTheFiveListFieldsAndResetsEverythingElse()
    {
        var source = NonDefault();
        var reset = source.ResetKeepingLists();
        var defaults = new ScanConfig();
        var kept = new[] { "Subnets", "PinnedIps", "InternetHosts", "ProfileNames", "ActiveProfile" };

        foreach (var p in SettableProps())
        {
            var expected = kept.Contains(p.Name) ? p.GetValue(source) : p.GetValue(defaults);
            AssertEqual(p, expected, p.GetValue(reset));
        }
        Assert.NotSame(source.Subnets, reset.Subnets);
        Assert.NotSame(source.ProfileNames, reset.ProfileNames);
    }
}
