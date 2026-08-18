using System.Buffers.Binary;
using DataGateOpenVpnManager.Services.Proxy;

namespace DataGateOpenVpnManager.Tests.Services.Proxy;

public class UdpWsFramingTests
{
    [Fact]
    public void WriteFrame_WritesLengthPrefix_BigEndian()
    {
        var payload = new byte[1400];
        payload[0] = 0xAB;
        var dest = new byte[2 + payload.Length];

        var written = UdpWsFraming.WriteFrame(dest, payload);

        Assert.Equal(1402, written);
        Assert.Equal(0x05, dest[0]);
        Assert.Equal(0x78, dest[1]);
        Assert.Equal(0xAB, dest[2]);
    }

    [Fact]
    public void WriteFrame_PacksMultipleFrames_Contiguously()
    {
        var dest = new byte[64];
        var offset = 0;
        offset += UdpWsFraming.WriteFrame(dest.AsSpan(offset), new byte[] { 1, 2 });
        offset += UdpWsFraming.WriteFrame(dest.AsSpan(offset), new byte[] { 3 });
        offset += UdpWsFraming.WriteFrame(dest.AsSpan(offset), new byte[] { 4, 5, 6 });

        Assert.Equal(2 + 2 + 2 + 1 + 2 + 3, offset);

        var parsed = new List<byte[]>();
        var off = 0;
        while (off < offset)
        {
            var next = UdpWsFraming.TryParseNextFrame(dest.AsSpan(0, offset), off, out var frame);
            Assert.True(next > 0);
            parsed.Add(frame.ToArray());
            off = next;
        }

        Assert.Equal(3, parsed.Count);
        Assert.Equal(new byte[] { 1, 2 }, parsed[0]);
        Assert.Equal(new byte[] { 3 }, parsed[1]);
        Assert.Equal(new byte[] { 4, 5, 6 }, parsed[2]);
    }

    [Fact]
    public void TryParseNextFrame_StopsOnTruncatedLengthPrefix()
    {
        var data = new byte[] { 0x00 };
        Assert.Equal(-1, UdpWsFraming.TryParseNextFrame(data, 0, out _));
    }

    [Fact]
    public void TryParseNextFrame_StopsOnLengthBeyondPayload()
    {
        var data = new byte[] { 0x00, 0x05, 1, 2, 3 };
        Assert.Equal(-1, UdpWsFraming.TryParseNextFrame(data, 0, out _));
    }

    [Fact]
    public void TryParseNextFrame_TreatsZeroLengthFrameAsInvalid()
    {
        var data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0xFF };
        Assert.Equal(-1, UdpWsFraming.TryParseNextFrame(data, 0, out _));
    }

    [Fact]
    public void TryParseNextFrame_HandlesMaxLength_65535()
    {
        var payload = new byte[65535];
        payload[^1] = 0x7E;
        var data = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, 65535);
        payload.CopyTo(data.AsSpan(2));

        var next = UdpWsFraming.TryParseNextFrame(data, 0, out var frame);
        Assert.Equal(data.Length, next);
        Assert.Equal(65535, frame.Length);
        Assert.Equal(0x7E, frame[^1]);
    }

    [Theory]
    [InlineData(0, 100, true)]
    [InlineData(UdpWsFraming.BatchTargetBytes - 1, 100, true)]
    [InlineData(UdpWsFraming.BatchTargetBytes, 100, false)]
    [InlineData(UdpWsFraming.BatchCapacityBytes - 10, 20, false)]
    public void CanAppendFrame_RespectsTargetAndCapacity(int offset, int payloadLen, bool expected)
    {
        Assert.Equal(
            expected,
            UdpWsFraming.CanAppendFrame(offset, payloadLen, UdpWsFraming.BatchCapacityBytes, UdpWsFraming.BatchTargetBytes));
    }

    [Fact]
    public void CanAppendFrame_AllowsFirstFrameEvenAboveTarget()
    {
        // First frame after flush: offset==0 bypasses target check so a single datagram always ships.
        Assert.True(UdpWsFraming.CanAppendFrame(
            0, UdpWsFraming.BatchTargetBytes + 100, UdpWsFraming.BatchCapacityBytes, UdpWsFraming.BatchTargetBytes));
    }

    [Fact]
    public void CanAppendFrame_FitsMaxPayload_OnEmptyBatch()
    {
        // Regression: BatchCapacityBytes must be >= 2 + MaxPayloadLength (was 65536 vs 65537).
        Assert.True(UdpWsFraming.BatchCapacityBytes >= UdpWsFraming.MaxFrameBytes);
        Assert.True(UdpWsFraming.CanAppendFrame(
            0, UdpWsFraming.MaxPayloadLength, UdpWsFraming.BatchCapacityBytes, UdpWsFraming.BatchTargetBytes));

        var dest = new byte[UdpWsFraming.BatchCapacityBytes];
        var payload = new byte[UdpWsFraming.MaxPayloadLength];
        payload[^1] = 0x42;
        var written = UdpWsFraming.WriteFrame(dest, payload);
        Assert.Equal(UdpWsFraming.MaxFrameBytes, written);
        Assert.Equal(0x42, dest[^1]);
    }
}
