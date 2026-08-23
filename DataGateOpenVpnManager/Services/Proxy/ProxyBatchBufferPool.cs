using System.Collections.Concurrent;

namespace DataGateOpenVpnManager.Services.Proxy;

/// <summary>
/// Fixed-size batch buffers for UDP→WS (exact <see cref="UdpWsFraming.BatchCapacityBytes"/>,
/// avoids ArrayPool bucket rounding into LOH).
/// </summary>
public sealed class ProxyBatchBufferPool
{
    public const int BufferSize = UdpWsFraming.BatchCapacityBytes;
    private const int MaxPooled = 1024;

    private readonly ConcurrentBag<byte[]> _bag = new();
    private int _pooledCount;
    private int _rented;
    private int _returned;

    public int Rented => Volatile.Read(ref _rented);
    public int Returned => Volatile.Read(ref _returned);

    public byte[] Rent()
    {
        Interlocked.Increment(ref _rented);
        if (_bag.TryTake(out var buffer))
        {
            Interlocked.Decrement(ref _pooledCount);
            return buffer;
        }

        return GC.AllocateUninitializedArray<byte>(BufferSize);
    }

    public void Return(byte[] buffer)
    {
        if (buffer.Length != BufferSize)
            return;

        Interlocked.Increment(ref _returned);
        if (Interlocked.Increment(ref _pooledCount) <= MaxPooled)
        {
            _bag.Add(buffer);
            return;
        }

        Interlocked.Decrement(ref _pooledCount);
    }
}
