using DataGateOpenVpnManager.Models;

namespace DataGateOpenVpnManager.Services.Interfaces;

public interface IOpenVpnProcessService
{
    Task<OpenVpnProcessStatusResponse> GetStatusAsync(CancellationToken cancellationToken);
    Task<OpenVpnProcessStatusResponse> StartAsync(CancellationToken cancellationToken);
    Task<OpenVpnProcessStatusResponse> KillAsync(CancellationToken cancellationToken);
    Task<OpenVpnProcessStatusResponse> RestartAsync(CancellationToken cancellationToken);
}
