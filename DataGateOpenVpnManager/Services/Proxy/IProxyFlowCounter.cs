using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;

namespace DataGateOpenVpnManager.Services.Proxy;

/// <summary>
/// Per-connection hot-path traffic counter (resolved once at pump start).
/// </summary>
public interface IProxyFlowCounter
{
    void Add(ProxyTrafficFlowDirection direction, long bytes);
}
