using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Services;

public class MicroserviceJwtValidatorInitializer(IMicroserviceJwtValidator validator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (validator is MicroserviceJwtValidator concrete)
            await concrete.InitAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
