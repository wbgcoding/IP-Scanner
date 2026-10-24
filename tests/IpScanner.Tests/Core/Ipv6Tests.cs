using System;
using System.Linq;
using IpScanner.Core.Net;
using Xunit;

namespace IpScanner.Tests.Core;

public class Ipv6Tests
{
    [Theory]
    [InlineData("::", "::")]
    [InlineData("::1", "::1")]
    [InlineData("0:0:0:0:0:0:0:1", "::1")]
    [InlineData("2001:0DB8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("2001:db8:0:0:0:1:0:0", "2001:db8::1:0:0")]
    [InlineData("2001:0:0:1:0:0:0:1", "2001:0:0:1::1")]
    [InlineData("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1")]
    [InlineData("2001:db8::0:1", "2001:db8::1")]
    [InlineData("1:0:0:0:0:0:0:0", "1::")]
    [InlineData("0:0:0:0:0:0:0:2", "::2")]
    [InlineData("FE80::A:B", "fe80::a:b")]
    [InlineData("::ffff:192.168.1.10", "::ffff:192.168.1.10")]
    [InlineData("::FFFF:C0A8:010A", "::ffff:192.168.1.10")]
    [InlineData("64:ff9b::1.2.3.4", "64:ff9b::102:304")]
    [InlineData("1:2:3:4:5:6:7::", "1:2:3:4:5:6:7:0")]
    [InlineData("::2:3:4:5:6:7:8", "0:2:3:4:5:6:7:8")]
    public void Parses_and_normalizes_per_rfc5952(string input, string expected)
    {
        Assert.True(Ipv6Address.TryParse(input, out var a));
        Assert.Equal(expected, a.ToString());
    }

    [Fact]
    public void A_single_zero_group_is_not_compressed()
    {
        Assert.Equal("2001:db8:0:1:1:1:1:1", Ipv6Address.Parse("2001:db8::1:1:1:1:1").ToString());
        Assert.Equal("1:0:3:4:5:6:7:8", Ipv6Address.Parse("1::3:4:5:6:7:8").ToString());
    }

    [Fact]
    public void The_longest_zero_run_wins_and_the_first_wins_a_tie()
    {
        Assert.Equal("1:0:0:2::3", Ipv6Address.Parse("1:0:0:2:0:0:0:3").ToString());
        Assert.Equal("1::2:0:0:3:0", Ipv6Address.Parse("1:0:0:2:0:0:3:0").ToString());
        Assert.Equal("1::3:4:0:0:5", Ipv6Address.Parse("1:0:0:3:4:0:0:5").ToString());
    }

    [Theory]
    [InlineData("fe80::1%12", "12")]
    [InlineData("fe80::1%Ethernet_2", "Ethernet_2")]
    [InlineData("fe80::1%wlan0.1-b", "wlan0.1-b")]
    public void Zone_ids_are_kept_apart_from_the_address(string input, string zone)
    {
        Assert.True(Ipv6Address.TryParse(input, out var a));
        Assert.Equal(zone, a.Zone);
        Assert.Equal("fe80::1", a.ToString());
        Assert.Equal("fe80::1%" + zone, a.ToScopedString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(":")]
    [InlineData(":::")]
    [InlineData("1:::2")]
    [InlineData("1::2::3")]
    [InlineData(":1:2:3:4:5:6:7")]
    [InlineData("1:2:3:4:5:6:7:")]
    [InlineData("1:2:3:4:5:6:7")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("1:2:3:4:5:6:7:8::")]
    [InlineData("::1:2:3:4:5:6:7:8")]
    [InlineData("12345::1")]
    [InlineData("g::1")]
    [InlineData("1::2:xyz")]
    [InlineData("[::1]")]
    [InlineData("::1/64")]
    [InlineData("::1 ")]
    [InlineData(" ::1")]
    [InlineData("::ffff:1.2.3")]
    [InlineData("::ffff:1.2.3.256")]
    [InlineData("::ffff:01.2.3.4")]
    [InlineData("::1.2.3.4:5")]
    [InlineData("1.2.3.4::")]
    [InlineData("fe80::1%")]
    [InlineData("fe80::1%a b")]
    [InlineData("fe80::1%a%b")]
    [InlineData("fe80::1%zone/64")]
    [InlineData("192.168.1.1")]
    [InlineData("+1::")]
    [InlineData("0x1::")]
    public void Invalid_input_is_rejected(string input)
    {
        Assert.False(Ipv6Address.TryParse(input, out _));
    }

    [Fact]
    public void Null_and_over_long_zones_are_rejected()
    {
        Assert.False(Ipv6Address.TryParse(null, out _));
        Assert.False(Ipv6Address.TryParse("fe80::1%" + new string('a', 65), out _));
        Assert.True(Ipv6Address.TryParse("fe80::1%" + new string('a', 64), out _));
    }

    [Fact]
    public void Round_trips_through_bytes()
    {
        var a = Ipv6Address.Parse("2001:db8:1:2:3:4:5:6");
        var b = Ipv6Address.FromBytes(a.GetBytes());
        Assert.Equal(a, b);
        Assert.Equal(0x20, a.GetBytes()[0]);
        Assert.Equal(0x06, a.GetBytes()[15]);
        Assert.Throws<ArgumentException>(() => Ipv6Address.FromBytes(new byte[4]));
    }

    [Theory]
    [InlineData("::", Ipv6Kind.Unspecified)]
    [InlineData("::1", Ipv6Kind.Loopback)]
    [InlineData("fe80::1", Ipv6Kind.LinkLocal)]
    [InlineData("febf::1", Ipv6Kind.LinkLocal)]
    [InlineData("fec0::1", Ipv6Kind.Reserved)]
    [InlineData("fc00::1", Ipv6Kind.UniqueLocal)]
    [InlineData("fd12:3456:789a::1", Ipv6Kind.UniqueLocal)]
    [InlineData("ff02::1", Ipv6Kind.Multicast)]
    [InlineData("ff02::fb", Ipv6Kind.Multicast)]
    [InlineData("::ffff:10.0.0.1", Ipv6Kind.IPv4Mapped)]
    [InlineData("2001:db8::1", Ipv6Kind.Documentation)]
    [InlineData("2001:4860:4860::8888", Ipv6Kind.Global)]
    [InlineData("2a00:1450::1", Ipv6Kind.Global)]
    [InlineData("3fff::1", Ipv6Kind.Global)]
    [InlineData("4000::1", Ipv6Kind.Reserved)]
    [InlineData("100::1", Ipv6Kind.Reserved)]
    public void Classifies_by_the_leading_bits(string text, Ipv6Kind expected)
    {
        Assert.Equal(expected, Ipv6Address.Parse(text).Kind);
    }

    [Fact]
    public void Mapped_addresses_expose_their_ipv4_part()
    {
        Assert.Equal("10.1.2.3", Ipv6Address.Parse("::ffff:10.1.2.3").MappedIpv4);
        Assert.Null(Ipv6Address.Parse("fe80::1").MappedIpv4);
    }

    [Theory]
    [InlineData("2001:db8::/32", "2001:db8::", 32)]
    [InlineData("2001:db8::1/64", "2001:db8::", 64)]
    [InlineData("2001:DB8:0:0:ffff::5/48", "2001:db8::", 48)]
    [InlineData("fe80::1/10", "fe80::", 10)]
    [InlineData("::/0", "::", 0)]
    [InlineData("::1/128", "::1", 128)]
    [InlineData("2001:db8::7", "2001:db8::7", 128)]
    public void Parses_prefixes_and_clears_host_bits(string input, string network, int length)
    {
        Assert.True(Ipv6Prefix.TryParse(input, out var p));
        Assert.Equal(network, p.First.ToString());
        Assert.Equal(length, p.Length);
        Assert.Equal(network + "/" + length, p.ToString());
    }

    [Theory]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8::/-1")]
    [InlineData("2001:db8::/")]
    [InlineData("2001:db8::/064")]
    [InlineData("2001:db8::/6x")]
    [InlineData("2001:db8::/64/64")]
    [InlineData("fe80::1%12/64")]
    [InlineData("10.0.0.0/8")]
    [InlineData("")]
    public void Invalid_prefixes_are_rejected(string input)
    {
        Assert.False(Ipv6Prefix.TryParse(input, out _));
    }

    [Fact]
    public void Prefix_contains_addresses_and_prefixes()
    {
        var net = Ipv6Prefix.TryParse("2001:db8:aaaa::/48", out var p) ? p : default;
        Assert.True(net.Contains(Ipv6Address.Parse("2001:db8:aaaa::")));
        Assert.True(net.Contains(Ipv6Address.Parse("2001:db8:aaaa:ffff:ffff:ffff:ffff:ffff")));
        Assert.False(net.Contains(Ipv6Address.Parse("2001:db8:aaab::")));
        Assert.False(net.Contains(Ipv6Address.Parse("2001:db8:aaa9:ffff:ffff:ffff:ffff:ffff")));

        Assert.True(Ipv6Prefix.TryParse("2001:db8:aaaa:5::/64", out var inner));
        Assert.True(net.Contains(inner));
        Assert.False(inner.Contains(net));
        Assert.True(net.Contains(net));

        Assert.True(Ipv6Prefix.TryParse("::/0", out var all));
        Assert.True(all.Contains(Ipv6Address.Parse("ff02::1")));
        Assert.True(all.Contains(net));
    }

    [Fact]
    public void A_slash_128_contains_only_itself()
    {
        Assert.True(Ipv6Prefix.TryParse("2001:db8::5/128", out var one));
        Assert.True(one.Contains(Ipv6Address.Parse("2001:db8::5")));
        Assert.False(one.Contains(Ipv6Address.Parse("2001:db8::6")));
        Assert.Equal(one.First, one.Last);
    }

    [Fact]
    public void First_and_last_cover_the_whole_prefix()
    {
        Assert.True(Ipv6Prefix.TryParse("2001:db8::/112", out var p));
        Assert.Equal("2001:db8::", p.First.ToString());
        Assert.Equal("2001:db8::ffff", p.Last.ToString());
        Assert.True(Ipv6Prefix.TryParse("2001:db8::/32", out var wide));
        Assert.Equal("2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", wide.Last.ToString());
    }

    [Theory]
    [InlineData(128, 1UL, false)]
    [InlineData(112, 65536UL, false)]
    [InlineData(111, 65536UL, true)]
    [InlineData(104, 65536UL, true)]
    [InlineData(64, 65536UL, true)]
    [InlineData(0, 65536UL, true)]
    [InlineData(64, ulong.MaxValue, true)]
    public void Detects_prefixes_with_more_addresses_than_allowed(int length, ulong max, bool exceeds)
    {
        var p = new Ipv6Prefix(Ipv6Address.Parse("2001:db8::").Value, length);
        Assert.Equal(exceeds, p.ExceedsAddresses(max));
    }

    [Fact]
    public void Eui64_recovers_the_mac_with_the_universal_bit_flipped()
    {
        Assert.Equal("00:11:22:33:44:55", Ipv6Address.Parse("fe80::211:22ff:fe33:4455").TryGetEui64Mac());
        Assert.Equal("AC:DE:48:00:11:22", Ipv6Address.Parse("fe80::aede:48ff:fe00:1122").TryGetEui64Mac());
        Assert.Equal("52:54:00:12:34:56", Ipv6Address.Parse("2a00:1450:1:2:5054:ff:fe12:3456").TryGetEui64Mac());
    }

    [Fact]
    public void Eui64_is_null_for_other_interface_ids_and_scopes()
    {
        Assert.Null(Ipv6Address.Parse("fe80::1").TryGetEui64Mac());
        Assert.Null(Ipv6Address.Parse("fe80::a2b3:c4d5:e6f7:1829").TryGetEui64Mac());
        Assert.Null(Ipv6Address.Parse("ff02::1:ff33:4455").TryGetEui64Mac());
    }

    [Fact]
    public void Eui64_builds_the_address_from_a_mac()
    {
        var upper = Ipv6Address.Parse("fe80::").Value;
        var a = Ipv6Address.FromEui64(upper, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });
        Assert.Equal("fe80::211:22ff:fe33:4455", a.ToString());
        Assert.Equal("00:11:22:33:44:55", a.TryGetEui64Mac());
        Assert.Throws<ArgumentException>(() => Ipv6Address.FromEui64(upper, new byte[5]));
    }

    [Fact]
    public void Solicited_node_keeps_the_last_24_bits()
    {
        Assert.Equal("ff02::1:ff33:4455", Ipv6Address.Parse("fe80::211:22ff:fe33:4455").SolicitedNode().ToString());
        Assert.Equal("ff02::1:ff00:1", Ipv6Address.Parse("2001:db8::1").SolicitedNode().ToString());
    }

    [Fact]
    public void Reverse_name_is_nibble_reversed()
    {
        Assert.Equal("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa",
            Ipv6Address.Parse("2001:db8::1").ReverseName());
    }

    [Fact]
    public void All_nodes_is_ff02_1()
    {
        Assert.Equal("ff02::1", Ipv6Address.AllNodes.ToString());
    }
}

public class IpAddressKeyTests
{
    [Theory]
    [InlineData("192.168.1.5", IpFamily.V4, "192.168.1.5")]
    [InlineData("0.0.0.0", IpFamily.V4, "0.0.0.0")]
    [InlineData("FE80:0::0001", IpFamily.V6, "fe80::1")]
    [InlineData("fe80::1%7", IpFamily.V6, "fe80::1")]
    [InlineData("::ffff:1.2.3.4", IpFamily.V6, "::ffff:1.2.3.4")]
    public void Parses_both_families_to_normalized_text(string input, IpFamily family, string text)
    {
        Assert.True(IpAddressKey.TryParse(input, out var k));
        Assert.Equal(family, k.Family);
        Assert.Equal(text, k.Text);
        Assert.Equal(text, k.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hostname")]
    [InlineData("192.168.1")]
    [InlineData("192.168.001.5")]
    [InlineData("192.168.1.256")]
    [InlineData(" 192.168.1.5")]
    [InlineData("+1.2.3.4")]
    [InlineData("1:2")]
    [InlineData("::g")]
    public void Rejects_what_is_no_address(string input)
    {
        Assert.False(IpAddressKey.TryParse(input, out _));
    }

    [Fact]
    public void The_zone_is_not_part_of_equality()
    {
        IpAddressKey.TryParse("fe80::1%3", out var a);
        IpAddressKey.TryParse("fe80::1%9", out var b);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("3", a.Zone);
    }

    [Fact]
    public void Families_never_compare_equal_even_for_the_same_number()
    {
        IpAddressKey.TryParse("0.0.0.1", out var v4);
        IpAddressKey.TryParse("::1", out var v6);
        Assert.NotEqual(v4, v6);
        Assert.True(v4 < v6);
    }

    [Fact]
    public void Orders_ipv4_first_then_numerically_inside_each_family()
    {
        var input = new[] { "2001:db8::2", "10.0.0.10", "::1", "10.0.0.9", "fe80::1", "2001:db8::1", "192.168.0.1" };
        var sorted = input.OrderBy(s => s, IpAddressKey.TextComparer).ToArray();
        Assert.Equal(new[] { "10.0.0.9", "10.0.0.10", "192.168.0.1", "::1", "2001:db8::1", "2001:db8::2", "fe80::1" }, sorted);
    }

    [Fact]
    public void Text_that_is_no_address_sorts_after_all_addresses()
    {
        var sorted = new[] { "host", "::1", "1.1.1.1" }.OrderBy(s => s, IpAddressKey.TextComparer).ToArray();
        Assert.Equal(new[] { "1.1.1.1", "::1", "host" }, sorted);
    }

    [Fact]
    public void Normalize_leaves_non_addresses_untouched()
    {
        Assert.Equal("fe80::1", IpAddressKey.Normalize("FE80:0:0::1"));
        Assert.Equal("printer.lan", IpAddressKey.Normalize("printer.lan"));
    }

    [Fact]
    public void Default_key_is_not_valid()
    {
        Assert.False(default(IpAddressKey).IsValid);
        Assert.True(IpAddressKey.FromV4(1).IsValid);
    }
}
