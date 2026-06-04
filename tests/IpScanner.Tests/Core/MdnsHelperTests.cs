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
    public void ParsePtrAnswer_GarbageInput_ReturnsNull()
    {
        Assert.Null(MdnsHelper.ParsePtrAnswer(new byte[] { 1, 2, 3 }));
        Assert.Null(MdnsHelper.ParsePtrAnswer(Enumerable.Repeat((byte)0xFF, 40).ToArray()));
    }
}
