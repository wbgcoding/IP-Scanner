using System.Collections.Generic;
using System.Linq;
using System.Text;
using IpScanner.Core.Scanner;
using Xunit;

namespace IpScanner.Tests.Core;

public class MdnsHelperTests
{
    [Fact]
    public void BuildPtrQuery_EncodesReverseArpaName()
    {
        var q = MdnsHelper.BuildPtrQuery("192.168.1.42");
        var text = Encoding.ASCII.GetString(q);
        Assert.Contains("42", text);
        Assert.Contains("168", text);
        Assert.Contains("in-addr", text);
        Assert.Contains("arpa", text);
        // QTYPE PTR (12), QCLASS IN with QU bit (0x8001) at the tail.
        Assert.Equal(12, (q[^4] << 8) | q[^3]);
        Assert.Equal(0x8001, (q[^2] << 8) | q[^1]);
    }

    [Fact]
    public void ParsePtrAnswer_ReadsName_AndStripsLocal()
    {
        // Synthetic response: 1 question (compressed away), 1 PTR answer "mydevice.local".
        var bytes = new List<byte>
        {
            0, 0, 0x84, 0,    // header: response flags
            0, 0,             // QDCOUNT 0
            0, 1,             // ANCOUNT 1
            0, 0, 0, 0,       // NS/AR
        };
        // answer name: "1.1.168.192.in-addr.arpa" (uncompressed)
        foreach (var label in "1.1.168.192.in-addr.arpa".Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 12 });        // TYPE PTR
        bytes.AddRange(new byte[] { 0x80, 1 });      // CLASS IN (cache-flush)
        bytes.AddRange(new byte[] { 0, 0, 0, 120 }); // TTL
        var rdata = new List<byte>();
        foreach (var label in new[] { "mydevice", "local" })
        {
            rdata.Add((byte)label.Length);
            rdata.AddRange(Encoding.ASCII.GetBytes(label));
        }
        rdata.Add(0);
        bytes.AddRange(new byte[] { (byte)(rdata.Count >> 8), (byte)rdata.Count });
        bytes.AddRange(rdata);

        var name = MdnsHelper.ParsePtrAnswer(bytes.ToArray());
        Assert.Equal("mydevice", name);
    }

    [Fact]
    public void ParsePtrAnswer_RdataNameLengthByteExceedsBuffer_ReturnsNullInsteadOfThrowing()
    {
        // The PTR answer's rdata name claims a label of length 8
        // ("mydevice") but the packet is cut off after 2 of those bytes - a
        // truncated/malicious reply, not valid mDNS. ReadName must stop at
        // the buffer edge rather than let Encoding.GetString throw.
        var bytes = new List<byte>
        {
            0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0,
        };
        foreach (var label in "1.1.168.192.in-addr.arpa".Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange(new byte[] { 0, 12, 0x80, 1, 0, 0, 0, 120 });
        bytes.AddRange(new byte[] { 0, 10 });                 // RDLENGTH claims 10 bytes...
        bytes.Add(8);                                          // ...but rdata is just this length byte (8)
        bytes.AddRange(Encoding.ASCII.GetBytes("my"));         // and 2 bytes, not the promised 8

        Assert.Null(MdnsHelper.ParsePtrAnswer(bytes.ToArray()));
    }

    [Fact]
    public void ParsePtrAnswer_GarbageInput_ReturnsNull()
    {
        Assert.Null(MdnsHelper.ParsePtrAnswer(new byte[] { 1, 2, 3 }));
        Assert.Null(MdnsHelper.ParsePtrAnswer(Enumerable.Repeat((byte)0xFF, 40).ToArray()));
    }

    [Fact]
    public void BuildPtrQueryV6_EncodesNibbleReversedName()
    {
        var q = MdnsHelper.BuildPtrQueryV6("fe80::1%12");
        var text = Encoding.ASCII.GetString(q);
        Assert.Contains("ip6", text);
        Assert.Contains("arpa", text);
        // 32 nibble labels of length 1, then "ip6" and "arpa".
        int pos = 12;
        for (int i = 0; i < 32; i++) { Assert.Equal(1, q[pos]); pos += 2; }
        Assert.Equal(3, q[pos]);
        Assert.Equal(1, q[12 + 1] == (byte)'1' ? 1 : 0);   // first label is the last nibble of ::1
    }

    [Fact]
    public void SameHost_IgnoresZoneAndFamilyMismatch()
    {
        var a = System.Net.IPAddress.Parse("fe80::1%12");
        var b = System.Net.IPAddress.Parse("fe80::1");
        Assert.True(MdnsHelper.SameHost(a, b));
        Assert.False(MdnsHelper.SameHost(a, System.Net.IPAddress.Parse("fe80::2")));
        Assert.False(MdnsHelper.SameHost(b, System.Net.IPAddress.Parse("0.0.0.1")));
    }
}
