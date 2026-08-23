using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;

namespace DataGateOpenVpnManager.Tests.Services.Proxy;

public class ProxyManagementPeerDiagnosticsTests
{
    [Fact]
    public void CanEvaluatePeerPresence_ReturnsFalse_WhenCacheEmpty()
    {
        var canEvaluate = ProxyManagementPeerDiagnostics.CanEvaluatePeerPresence(
            null,
            new OpenVpnProxyOptions { ManagementStatusRefreshSeconds = 30 },
            out var skipReason);

        Assert.False(canEvaluate);
        Assert.Equal("management_cache_unavailable", skipReason);
    }

    [Fact]
    public void NeedsRefreshForClientMapping_True_WhenEmptyOrStale()
    {
        Assert.True(ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
            null,
            TimeSpan.FromSeconds(60)));

        Assert.True(ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
            new OpenVpnManagementStatusSnapshot(DateTime.UtcNow, "END", [], true),
            TimeSpan.FromSeconds(60)));

        Assert.True(ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
            new OpenVpnManagementStatusSnapshot(
                DateTime.UtcNow.AddHours(-1),
                "END",
                [new OpenVpnManagementClientEntry("cn", "1.1.1.1:1", "10.0.0.2", 0, 0, 0)],
                true),
            TimeSpan.FromSeconds(60)));

        Assert.False(ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
            new OpenVpnManagementStatusSnapshot(
                DateTime.UtcNow,
                "END",
                [new OpenVpnManagementClientEntry("cn", "1.1.1.1:1", "10.0.0.2", 0, 0, 0)],
                true),
            TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void IsLikelyZombie_ReturnsFalse_WhenCacheOlderThanConnection()
    {
        var snapshot = new OpenVpnManagementStatusSnapshot(
            DateTime.UtcNow.AddMinutes(-1),
            "END",
            Array.Empty<OpenVpnManagementClientEntry>(),
            IsValid: true);
        var connection = new ActiveProxyConnection
        {
            ConnectionId = "c1",
            Protocol = ProxyConnectionProtocol.Udp,
            RealClientIp = "127.0.0.1",
            RealClientPort = 1,
            LocalProxyIp = "127.0.0.1",
            LocalProxyPort = 60123,
            TargetIp = "127.0.0.1",
            TargetPort = 1194,
            ConnectedAtUtc = DateTime.UtcNow
        };

        Assert.False(ProxyManagementPeerDiagnostics.IsLikelyZombie(connection, null, snapshot));
    }
}
