using System.Buffers.Binary;
using Nite.Esbuild;

namespace Nite.Tests;

public class EsbuildPacketTests
{
    [Fact]
    public void Round_trips_every_value_type()
    {
        var value = new Dictionary<string, object?>
        {
            ["command"] = "transform",
            ["flags"] = new List<object?> { "--loader=tsx", "--format=esm" },
            ["inputFS"] = false,
            ["key"] = 42,
            ["input"] = new byte[] { 1, 2, 3 },
            ["nothing"] = null,
            ["unicode"] = "→ ✓",
        };

        var packet = EsbuildPacket.Encode(7, isRequest: true, value);
        Assert.Equal((uint)(packet.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(packet));

        var (id, isRequest, decoded) = EsbuildPacket.Decode(packet.AsSpan(4));
        Assert.Equal(7u, id);
        Assert.True(isRequest);

        var map = Assert.IsType<Dictionary<string, object?>>(decoded);
        Assert.Equal("transform", map["command"]);
        Assert.Equal(new List<object?> { "--loader=tsx", "--format=esm" }, map["flags"]);
        Assert.Equal(false, map["inputFS"]);
        Assert.Equal(42, map["key"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, map["input"]);
        Assert.Null(map["nothing"]);
        Assert.Equal("→ ✓", map["unicode"]);
    }

    [Fact]
    public void Encodes_responses_with_the_low_bit_set()
    {
        var packet = EsbuildPacket.Encode(3, isRequest: false, new Dictionary<string, object?>());
        Assert.Equal((3u << 1) | 1, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.False(EsbuildPacket.Decode(packet.AsSpan(4)).IsRequest);
    }

    [Fact]
    public void Rejects_truncated_packets()
    {
        var packet = EsbuildPacket.Encode(1, isRequest: true, "hello");
        Assert.Throws<InvalidDataException>(() => EsbuildPacket.Decode(packet.AsSpan(4, packet.Length - 6)));
    }
}
