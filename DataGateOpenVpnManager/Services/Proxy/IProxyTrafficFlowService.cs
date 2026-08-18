using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;

namespace DataGateOpenVpnManager.Services.Proxy;

public interface IProxyTrafficFlowService
{
    void RegisterConnection(ActiveProxyConnection connection, ProxyConnectionIdentity? identity = null);
    ProxyTrafficFlowUpdate? UnregisterConnection(string connectionId, DateTime? disconnectedAtUtc = null);
    bool TryGetTotals(string connectionId, out long clientToServerBytesTotal, out long serverToClientBytesTotal);
    void RegisterConnectFailed(
        string connectionId,
        ProxyConnectionProtocol protocol,
        string? realClientIp,
        int realClientPort,
        ProxyConnectionIdentity? identity,
        string targetIp,
        int targetPort,
        string? errorMessage,
        DateTime? failedAtUtc = null);
    void RecordTraffic(
        string connectionId,
        ProxyTrafficFlowDirection direction,
        int bytes,
        DateTime? occurredAtUtc = null);
    IReadOnlyCollection<ProxyTrafficFlowUpdate> BuildBatch(DateTime emittedAtUtc);

    /// <summary>Hot-path counter resolved once per pump (avoids dictionary lookup per packet).</summary>
    IProxyFlowCounter? GetCounter(string connectionId);

    bool TryGetIdentityByLocalProxy(int localProxyPort, string? host, out ProxyTrafficIdentitySnapshot? identity);
}
