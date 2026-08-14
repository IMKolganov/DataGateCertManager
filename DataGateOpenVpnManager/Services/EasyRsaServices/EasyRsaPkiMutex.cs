using System.Collections.Concurrent;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;

namespace DataGateOpenVpnManager.Services.EasyRsaServices;

public sealed class EasyRsaPkiMutex : IEasyRsaPkiMutex
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path currently held by this async flow (re-entrancy).</summary>
    private static readonly AsyncLocal<string?> HeldPath = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string easyRsaPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(easyRsaPath);
        var key = Path.GetFullPath(easyRsaPath);

        if (string.Equals(HeldPath.Value, key, StringComparison.OrdinalIgnoreCase))
            return Noop.Instance;

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        HeldPath.Value = key;
        return new Releaser(gate, key);
    }

    private sealed class Releaser(SemaphoreSlim gate, string key) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;

            if (string.Equals(HeldPath.Value, key, StringComparison.OrdinalIgnoreCase))
                HeldPath.Value = null;

            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Noop : IAsyncDisposable
    {
        public static readonly Noop Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
