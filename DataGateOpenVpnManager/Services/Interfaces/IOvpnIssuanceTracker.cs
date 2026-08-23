namespace DataGateOpenVpnManager.Services.Interfaces;

/// <summary>
/// Tracks in-flight OVPN issuances so download can wait instead of failing while EasyRSA is busy.
/// </summary>
public interface IOvpnIssuanceTracker
{
    /// <summary>Registers <paramref name="commonName"/> as issuing until the lease completes.</summary>
    IOvpnIssuanceLease Begin(string commonName);

    /// <summary>
    /// Waits until <paramref name="filePath"/> exists, or until an in-flight issuance for
    /// <paramref name="commonName"/> finishes. Throws if issuance fails or wait times out.
    /// </summary>
    Task WaitUntilReadyAsync(string commonName, string filePath, CancellationToken cancellationToken);
}

public interface IOvpnIssuanceLease : IAsyncDisposable
{
    void MarkCompleted();
    void MarkFailed(Exception exception);
}
