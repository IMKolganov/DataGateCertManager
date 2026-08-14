using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateOpenVpnManager.Models;

namespace DataGateOpenVpnManager.Services.Proxy;

internal static class ProxyManagementPeerDiagnostics
{
    public static bool CanEvaluatePeerPresence(
        OpenVpnManagementStatusSnapshot? snapshot,
        OpenVpnProxyOptions options,
        out string? skipReason)
    {
        if (snapshot is null || !snapshot.IsValid)
        {
            skipReason = "management_cache_unavailable";
            return false;
        }

        var cacheAge = DateTime.UtcNow - snapshot.FetchedAtUtc;
        if (cacheAge > options.ManagementCacheMaxAge)
        {
            skipReason = "management_cache_stale";
            return false;
        }

        if (snapshot.Clients.Count == 0)
        {
            skipReason = "client_list_empty";
            return false;
        }

        skipReason = null;
        return true;
    }

    /// <summary>
    /// True when the Pi-hole (or other) consumer should call <c>RefreshAsync</c> before IP→CN mapping.
    /// An empty-but-"valid" snapshot must not be trusted forever — that stuck the DNS collector in production.
    /// </summary>
    public static bool NeedsRefreshForClientMapping(
        OpenVpnManagementStatusSnapshot? snapshot,
        TimeSpan maxAge)
    {
        if (snapshot is null || !snapshot.IsValid)
            return true;

        if (snapshot.Clients.Count == 0)
            return true;

        return DateTime.UtcNow - snapshot.FetchedAtUtc > maxAge;
    }

    public static bool IsLikelyZombie(
        ActiveProxyConnection connection,
        OpenVpnManagementClientEntry? mgmtClient,
        OpenVpnManagementStatusSnapshot snapshot)
    {
        if (mgmtClient is not null)
            return false;

        if (snapshot.FetchedAtUtc < connection.ConnectedAtUtc)
            return false;

        return true;
    }
}
