using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.PiHole;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Services.Proxy;

public sealed class OpenVpnManagementStatusRefreshService(
    IOptions<OpenVpnProxyOptions> options,
    IPiHoleRuntimeOptionsStore piHoleRuntime,
    IOpenVpnManagementStatusCache statusCache,
    ILogger<OpenVpnManagementStatusRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var piHoleEnabled = piHoleRuntime.GetEffective().Enabled
                                && !string.IsNullOrWhiteSpace(piHoleRuntime.GetEffective().BaseUrl);
            if (!options.Value.NeedsBackgroundManagementRefresh(piHoleEnabled))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var interval = Math.Max(5, options.Value.ManagementStatusRefreshSeconds);
            try
            {
                await statusCache.RefreshAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ManagementStatusCache] refresh loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
