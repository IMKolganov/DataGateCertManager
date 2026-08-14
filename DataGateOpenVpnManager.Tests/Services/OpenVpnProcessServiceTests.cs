using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DataGateOpenVpnManager.Tests.Services;

public class OpenVpnProcessServiceTests
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ovpn-proc-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IOpenVpnProcessRunner> _runner = new();

    public OpenVpnProcessServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "server.conf"), "port 1194\n");
    }

    private OpenVpnProcessService CreateSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIR"] = _dataDir })
            .Build();
        return new OpenVpnProcessService(config, _runner.Object, NullLogger<OpenVpnProcessService>.Instance);
    }

    [Fact]
    public async Task Status_WhenPidFileAlive_ReportsRunning()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "openvpn.pid"), "4242");
        _runner.Setup(r => r.IsProcessAlive(4242)).Returns(true);

        var status = await CreateSut().GetStatusAsync(CancellationToken.None);

        Assert.Equal("status", status.Action);
        Assert.True(status.IsRunning);
        Assert.Equal(4242, status.Pid);
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_DoesNotStartAgain()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "openvpn.pid"), "7");
        _runner.Setup(r => r.IsProcessAlive(7)).Returns(true);

        var status = await CreateSut().StartAsync(CancellationToken.None);

        Assert.Equal("start", status.Action);
        Assert.True(status.IsRunning);
        Assert.Contains("already running", status.Message, StringComparison.OrdinalIgnoreCase);
        _runner.Verify(r => r.Start(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Start_WhenStopped_StartsOpenVpn()
    {
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);
        _runner.Setup(r => r.Start(It.IsAny<string>(), It.IsAny<string>())).Returns(99);
        _runner.Setup(r => r.IsProcessAlive(99)).Returns(true);

        var status = await CreateSut().StartAsync(CancellationToken.None);

        Assert.True(status.IsRunning);
        Assert.Equal(99, status.Pid);
        _runner.Verify(r => r.Start(
            Path.Combine(_dataDir, "server.conf"),
            Path.Combine(_dataDir, "openvpn.pid")), Times.Once);
    }

    [Fact]
    public async Task Kill_WhenRunning_SendsTermThenClears()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "openvpn.pid"), "55");
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(() => alive);
        _runner.Setup(r => r.TrySignal(55, false)).Callback(() => alive = false).Returns(true);

        var status = await CreateSut().KillAsync(CancellationToken.None);

        Assert.Equal("kill", status.Action);
        Assert.False(status.IsRunning);
        Assert.Contains("stopped", status.Message, StringComparison.OrdinalIgnoreCase);
        _runner.Verify(r => r.TrySignal(55, false), Times.Once);
        Assert.False(File.Exists(Path.Combine(_dataDir, "openvpn.pid")));
    }

    [Fact]
    public async Task Restart_KillsThenStarts()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "openvpn.pid"), "11");
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(11)).Returns(() => alive);
        _runner.Setup(r => r.TrySignal(11, false)).Callback(() => alive = false).Returns(true);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.Start(It.IsAny<string>(), It.IsAny<string>())).Returns(22);
        _runner.Setup(r => r.IsProcessAlive(22)).Returns(true);

        var status = await CreateSut().RestartAsync(CancellationToken.None);

        Assert.Equal("restart", status.Action);
        Assert.True(status.IsRunning);
        Assert.Equal(22, status.Pid);
        _runner.Verify(r => r.TrySignal(11, false), Times.Once);
        _runner.Verify(r => r.Start(
            Path.Combine(_dataDir, "server.conf"),
            Path.Combine(_dataDir, "openvpn.pid")), Times.Once);
    }

    [Fact]
    public async Task Start_WhenConfigMissing_Throws()
    {
        File.Delete(Path.Combine(_dataDir, "server.conf"));
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            CreateSut().StartAsync(CancellationToken.None));
    }
}
