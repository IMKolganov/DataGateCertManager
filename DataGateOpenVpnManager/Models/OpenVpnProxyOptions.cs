namespace DataGateOpenVpnManager.Models;

public sealed class OpenVpnProxyOptions
{
    /// <summary>
    /// When enabled, logs per-connection proxy byte totals and compares them with OpenVPN management (status 3).
    /// Env: <c>OpenVpnProxy__ByteDebug=true</c> or <c>PROXY_BYTE_DEBUG=1</c>.
    /// </summary>
    public bool ByteDebug { get; set; }

    /// <summary>
    /// Structured investigation log: every proxy/management/zombie decision with context.
    /// Enabled automatically when <see cref="ByteDebug"/> is true.
    /// Env: <c>OpenVpnProxy__SessionAudit=true</c>.
    /// </summary>
    public bool SessionAudit { get; set; }

    /// <summary>
    /// In-memory audit ring buffer size exposed via /api/diagnostics/proxy-audit.
    /// </summary>
    public int SessionAuditBufferSize { get; set; } = 500;

    /// <summary>
    /// Optional periodic comparison interval in seconds for active connections. 0 = disconnect-only.
    /// Env: <c>OpenVpnProxy__ByteDebugIntervalSeconds</c>.
    /// </summary>
    public int ByteDebugIntervalSeconds { get; set; }

    /// <summary>
    /// Log a warning when proxy vs management delta exceeds this many bytes (per direction).
    /// </summary>
    public long ByteDebugWarnDeltaBytes { get; set; } = 4096;

    /// <summary>
    /// Close WSS proxy when the OpenVPN peer disappears from management for this long (seconds). 0 = disabled.
    /// Env: <c>OpenVpnProxy__CloseZombieAfterMissingSeconds</c>.
    /// </summary>
    public int CloseZombieAfterMissingSeconds { get; set; }

    /// <summary>
    /// Require this many consecutive cache misses before terminating a proxy session.
    /// </summary>
    public int ZombieMinConsecutiveMisses { get; set; } = 3;

    /// <summary>
    /// How often to check active proxy sessions against OpenVPN management.
    /// Env: <c>OpenVpnProxy__ZombieCheckIntervalSeconds</c>.
    /// </summary>
    public int ZombieCheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Tail <c>openvpn.log</c> and emit tagged TLS/tls-crypt lines for Wazuh
    /// (<c>[OpenVpnTlsExternalProbe]</c> vs <c>[OpenVpnTlsAppClient]</c>).
    /// Env: <c>OpenVpnProxy__TlsLogEnrichmentEnabled</c> (default true).
    /// </summary>
    public bool TlsLogEnrichmentEnabled { get; set; } = true;

    /// <summary>
    /// Single shared OpenVPN management status poll interval (seconds).
    /// Env: <c>OpenVpnProxy__ManagementStatusRefreshSeconds</c>.
    /// </summary>
    public int ManagementStatusRefreshSeconds { get; set; } = 30;

    internal bool IsSessionAuditEnabled => SessionAudit || ByteDebug;

    /// <summary>
    /// Background <c>status 3</c> polling is required for zombie detection, byte-debug,
    /// and Pi-hole DNS CN mapping (otherwise the cache can stay empty/stale forever).
    /// </summary>
    internal bool NeedsBackgroundManagementRefresh(bool piHoleCollectorEnabled) =>
        piHoleCollectorEnabled
        || CloseZombieAfterMissingSeconds > 0
        || ByteDebug && ByteDebugIntervalSeconds > 0;

    /// <summary>Max age before a management snapshot is considered stale for consumers.</summary>
    internal TimeSpan ManagementCacheMaxAge =>
        TimeSpan.FromSeconds(Math.Max(10, ManagementStatusRefreshSeconds * 2));
}
