namespace DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;

/// <summary>
/// Serializes EasyRSA PKI mutations (build/revoke/CRL/init). EasyRSA touches shared files
/// (<c>index.txt</c>, <c>serial</c>, <c>vars</c>) and is not safe under concurrent processes.
/// </summary>
public interface IEasyRsaPkiMutex
{
    /// <summary>
    /// Acquires an exclusive lock for the given EasyRSA root. Re-entrant on the same async context
    /// and path (so nested calls during an already-held lock do not deadlock).
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireAsync(string easyRsaPath, CancellationToken cancellationToken = default);
}
