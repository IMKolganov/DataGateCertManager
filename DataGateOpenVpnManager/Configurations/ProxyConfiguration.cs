using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.OpenVpnTls;
using DataGateOpenVpnManager.Services.Proxy;

namespace DataGateOpenVpnManager.Configurations;

public static class ProxyConfiguration
{
    public static void ConfigureProxy(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OpenVpnProxyOptions>(config.GetSection("OpenVpnProxy"));
        services.PostConfigure<OpenVpnProxyOptions>(options => ApplyLegacyByteDebugEnv(config, options));

        services.AddSingleton<IProxyConnectionHistoryService, ProxyConnectionHistoryService>();
        services.AddSingleton<IActiveProxyConnectionService, ActiveProxyConnectionService>();
        services.AddSingleton<IProxyConnectionIdentityResolver, ProxyConnectionIdentityResolver>();
        services.AddSingleton<IProxyTrafficFlowService, ProxyTrafficFlowService>();
        services.AddSingleton<ProxyBatchBufferPool>();
        services.AddSingleton<IProxyConnectionLifetimeService, ProxyConnectionLifetimeService>();
        services.AddSingleton<IProxySessionAuditService, ProxySessionAuditService>();
        services.AddSingleton<IOpenVpnManagementStatusCache, OpenVpnManagementStatusCache>();
        services.AddHostedService<OpenVpnManagementStatusRefreshService>();
        services.AddSingleton<ProxyByteDebugService>();
        services.AddSingleton<IProxyByteDebugService>(sp => sp.GetRequiredService<ProxyByteDebugService>());
        services.AddHostedService<ProxyTrafficFlowBroadcastService>();
        services.AddHostedService<ProxyByteDebugMonitorService>();
        services.AddHostedService<ProxyZombieConnectionMonitorService>();
        services.AddSingleton<IOpenVpnTlsErrorClassifier, OpenVpnTlsErrorClassifier>();
        services.AddHostedService<OpenVpnTlsLogEnrichmentHostedService>();
    }

    private static void ApplyLegacyByteDebugEnv(IConfiguration config, OpenVpnProxyOptions options)
    {
        if (options.ByteDebug)
            return;

        var legacy = config["PROXY_BYTE_DEBUG"];
        if (string.IsNullOrWhiteSpace(legacy))
            return;

        if (bool.TryParse(legacy, out var enabled))
        {
            options.ByteDebug = enabled;
            return;
        }

        options.ByteDebug = legacy is "1" or "yes" or "on";
    }
}