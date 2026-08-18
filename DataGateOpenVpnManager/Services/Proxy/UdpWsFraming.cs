using System.Buffers.Binary;

namespace DataGateOpenVpnManager.Services.Proxy;

/// <summary>
/// Wire format for OpenVPN-over-WebSocket UDP: one WS binary message may contain
/// one or more frames <c>[u16_be len][payload]</c>. Payload length is 1..65535.
/// </summary>
internal static class UdpWsFraming
{
    public const int MaxPayloadLength = 65535;
    /// <summary>One framed datagram: u16 length prefix + max payload.</summary>
    public const int MaxFrameBytes = 2 + MaxPayloadLength; // 65537
    /// <summary>
    /// Batch buffer must fit at least one max frame. Kept under LOH threshold (85_000).
    /// </summary>
    public const int BatchCapacityBytes = MaxFrameBytes;
    public const int BatchTargetBytes = 48 * 1024;

    public static int WriteFrame(Span<byte> dest, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= 0 or > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (dest.Length < 2 + payload.Length)
            throw new ArgumentException("Destination too small for frame.", nameof(dest));

        BinaryPrimitives.WriteUInt16BigEndian(dest, (ushort)payload.Length);
        payload.CopyTo(dest[2..]);
        return 2 + payload.Length;
    }

    /// <summary>
    /// Parses frames from a WS payload. Stops on truncated prefix, len==0, or len beyond remaining
    /// (matches historical proxy behaviour — do not silently skip).
    /// </summary>
    public static int TryParseNextFrame(ReadOnlySpan<byte> data, int offset, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (offset + 2 > data.Length)
            return -1;

        var len = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (len == 0 || offset + len > data.Length)
            return -1;

        payload = data.Slice(offset, len);
        return offset + len;
    }

    public static bool CanAppendFrame(int currentOffset, int payloadLen, int capacity, int targetBytes) =>
        payloadLen > 0
        && payloadLen <= MaxPayloadLength
        && currentOffset + 2 + payloadLen <= capacity
        && (currentOffset == 0 || currentOffset < targetBytes);
}
