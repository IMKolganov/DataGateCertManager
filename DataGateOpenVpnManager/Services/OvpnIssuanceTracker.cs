using System.Collections.Concurrent;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Services;

public sealed class OvpnIssuanceOptions
{
    public const string SectionName = "OvpnIssuance";

    /// <summary>Env override (also supports <c>OvpnIssuance__WaitTimeoutSeconds</c>).</summary>
    public const string WaitTimeoutSecondsEnvVar = "OVPN_ISSUANCE_WAIT_TIMEOUT_SECONDS";

    /// <summary>Default wait when download hits a missing file that is still being issued.</summary>
    public const int DefaultWaitTimeoutSeconds = 10;

    /// <summary>Max time download waits for an in-flight (or about-to-start) issuance.</summary>
    public int WaitTimeoutSeconds { get; set; } = DefaultWaitTimeoutSeconds;
}

public sealed class OvpnIssuanceTracker(IOptions<OvpnIssuanceOptions> options, ILogger<OvpnIssuanceTracker> logger)
    : IOvpnIssuanceTracker
{
    private readonly ConcurrentDictionary<string, IssuanceEntry> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public IOvpnIssuanceLease Begin(string commonName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        var key = commonName.Trim();
        var entry = new IssuanceEntry();
        if (!_inflight.TryAdd(key, entry))
            throw new InvalidOperationException($"Issuance already in progress for common name '{key}'.");

        logger.LogDebug("OVPN issuance started for {CommonName}", key);
        return new Lease(this, key, entry);
    }

    public async Task WaitUntilReadyAsync(string commonName, string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var key = commonName.Trim();
        filePath = Path.GetFullPath(filePath);
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.WaitTimeoutSeconds));
        var deadline = DateTime.UtcNow + timeout;

        logger.LogInformation(
            "Download waiting for OVPN {CommonName} at {FilePath} (timeout {TimeoutSeconds}s)",
            key, filePath, timeout.TotalSeconds);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(filePath))
            {
                logger.LogDebug("OVPN file ready for {CommonName}", key);
                return;
            }

            if (_inflight.TryGetValue(key, out var entry))
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                try
                {
                    await entry.Completion.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"OVPN issuance failed for '{key}' while waiting for download.", ex);
                }

                if (File.Exists(filePath))
                    return;

                throw new FileNotFoundException(
                    $"OVPN issuance finished for '{key}' but file was not found: {filePath}", filePath);
            }

            // Issuance not registered yet — brief poll so early download can catch Begin().
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:0}s waiting for OVPN '{key}' ({filePath}).");
    }

    private void Complete(string key, IssuanceEntry entry, Exception? error)
    {
        entry.Error = error;
        if (error is null)
            entry.Completion.TrySetResult();
        else
            entry.Completion.TrySetException(error);

        _inflight.TryRemove(new KeyValuePair<string, IssuanceEntry>(key, entry));
        logger.LogDebug(error, "OVPN issuance finished for {CommonName}", key);
    }

    private sealed class IssuanceEntry
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Exception? Error { get; set; }
    }

    private sealed class Lease(OvpnIssuanceTracker owner, string key, IssuanceEntry entry) : IOvpnIssuanceLease
    {
        private int _settled;

        public void MarkCompleted()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
                return;
            owner.Complete(key, entry, error: null);
        }

        public void MarkFailed(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.Exchange(ref _settled, 1) != 0)
                return;
            owner.Complete(key, entry, exception);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _settled, 1) == 0)
                owner.Complete(key, entry, new OperationCanceledException($"Issuance for '{key}' was abandoned."));
            return ValueTask.CompletedTask;
        }
    }
}
