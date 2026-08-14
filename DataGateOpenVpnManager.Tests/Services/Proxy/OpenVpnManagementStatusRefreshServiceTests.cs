using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.OpenVpnTelnet;
using DataGateOpenVpnManager.Services.PiHole;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateOpenVpnManager.Tests.Services.OpenVpnTelnet.Fakes;
using DataGateOpenVpnManager.Tests.Services.PiHole;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Tests.Services.Proxy;

public class OpenVpnManagementStatusRefreshServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenRefreshDisabled_IdlesWithoutCallingManagement()
    {
        var telnet = new FakeTelnetClient();
        var management = new OpenVpnManagementSignalService(telnet, NullLogger<CommandQueue>.Instance);
        var cache = new OpenVpnManagementStatusCache(management, new NoOpProxySessionAuditService(), NullLogger<OpenVpnManagementStatusCache>.Instance);
        var service = new OpenVpnManagementStatusRefreshService(
            Options.Create(new OpenVpnProxyOptions()),
            PiHoleRuntimeOptionsStoreTestHelper.Create(new PiHoleOptions { Enabled = false }),
            cache,
            NullLogger<OpenVpnManagementStatusRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(telnet.SentCommands);
    }

    [Fact]
    public async Task ExecuteAsync_WhenZombieEnabled_RefreshesManagement()
    {
        var telnet = new AutoRespondingFakeTelnetClient("END");
        var management = new OpenVpnManagementSignalService(telnet, NullLogger<CommandQueue>.Instance);
        var cache = new OpenVpnManagementStatusCache(management, new NoOpProxySessionAuditService(), NullLogger<OpenVpnManagementStatusCache>.Instance);
        var service = new OpenVpnManagementStatusRefreshService(
            Options.Create(new OpenVpnProxyOptions
            {
                CloseZombieAfterMissingSeconds = 60,
                ManagementStatusRefreshSeconds = 1
            }),
            PiHoleRuntimeOptionsStoreTestHelper.Create(new PiHoleOptions { Enabled = false }),
            cache,
            NullLogger<OpenVpnManagementStatusRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1200);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("status 3", telnet.SentCommands);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPiHoleEnabled_RefreshesManagement()
    {
        var telnet = new AutoRespondingFakeTelnetClient("END");
        var management = new OpenVpnManagementSignalService(telnet, NullLogger<CommandQueue>.Instance);
        var cache = new OpenVpnManagementStatusCache(management, new NoOpProxySessionAuditService(), NullLogger<OpenVpnManagementStatusCache>.Instance);
        var service = new OpenVpnManagementStatusRefreshService(
            Options.Create(new OpenVpnProxyOptions { ManagementStatusRefreshSeconds = 1 }),
            PiHoleRuntimeOptionsStoreTestHelper.Create(new PiHoleOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:8080"
            }),
            cache,
            NullLogger<OpenVpnManagementStatusRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(1200);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains("status 3", telnet.SentCommands);
    }

    private sealed class AutoRespondingFakeTelnetClient(string response) : FakeTelnetClient
    {
        public override Task SendAsync(string command, CancellationToken cancellationToken)
        {
            var task = base.SendAsync(command, cancellationToken);
            SimulateIncomingData(response);
            return task;
        }
    }
}
