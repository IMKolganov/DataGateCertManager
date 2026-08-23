using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DataGateOpenVpnManager.Tests.Services;

[Collection(nameof(OpenVpnProcessServiceTests))]
public class OpenVpnProcessServiceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ovpn-proc-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IOpenVpnProcessRunner> _runner = new();

    public OpenVpnProcessServiceTests()
    {
        OpenVpnProcessService.ResetStateForTests();
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, "server.conf"), "port 1194\n");
    }

    public void Dispose()
    {
        OpenVpnProcessService.ResetStateForTests();
        if (Directory.Exists(_dataDir))
            Directory.Delete(_dataDir, recursive: true);
    }

    private OpenVpnProcessService CreateSut()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DATA_DIR"] = _dataDir })
            .Build();
        return new OpenVpnProcessService(config, _runner.Object, NullLogger<OpenVpnProcessService>.Instance);
    }

    private string PidFile => Path.Combine(_dataDir, "openvpn.pid");
    private string ConfigFile => Path.Combine(_dataDir, "server.conf");

    private void SetupAlive(int pid, bool alive = true) =>
        _runner.Setup(r => r.IsProcessAlive(pid)).Returns(alive);

    [Fact]
    public async Task Status_WhenPidFileAlive_ReportsRunning()
    {
        await File.WriteAllTextAsync(PidFile, "4242");
        SetupAlive(4242);

        var status = await CreateSut().GetStatusAsync(CancellationToken.None);

        Assert.Equal("status", status.Action);
        Assert.True(status.IsRunning);
        Assert.Equal(4242, status.Pid);
        Assert.Contains("pid file present", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_WhenPidFileStale_ClearsFileAndReportsStopped()
    {
        await File.WriteAllTextAsync(PidFile, "4242");
        SetupAlive(4242, alive: false);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);

        var status = await CreateSut().GetStatusAsync(CancellationToken.None);

        Assert.False(status.IsRunning);
        Assert.Null(status.Pid);
        Assert.False(File.Exists(PidFile));
    }

    [Fact]
    public async Task Start_WhenAlreadyRunning_DoesNotStartAgain_AndKeepsPidFile()
    {
        await File.WriteAllTextAsync(PidFile, "7");
        SetupAlive(7);

        var status = await CreateSut().StartAsync(CancellationToken.None);

        Assert.True(status.IsRunning);
        Assert.Equal(7, status.Pid);
        Assert.Contains("already running", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(PidFile));
        _runner.Verify(r => r.Start(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Start_WhenStopped_StartsAndRequiresLiveProcessPlusPidFile()
    {
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);
        _runner.Setup(r => r.Start(ConfigFile, PidFile))
            .Callback<string, string>((_, pidFile) =>
            {
                File.WriteAllText(pidFile, "99");
                _runner.Setup(r => r.IsProcessAlive(99)).Returns(true);
            })
            .Returns(99);

        var status = await CreateSut().StartAsync(CancellationToken.None);

        Assert.True(status.IsRunning);
        Assert.Equal(99, status.Pid);
        Assert.True(File.Exists(PidFile));
        Assert.Equal("99", (await File.ReadAllTextAsync(PidFile)).Trim());
        Assert.Contains("pid file present", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WhenProcessDoesNotStayUp_Throws()
    {
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);
        _runner.Setup(r => r.Start(It.IsAny<string>(), It.IsAny<string>())).Returns(55);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().StartAsync(CancellationToken.None));

        Assert.Contains("failed verification", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pid file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WhenAliveButPidFileMissing_WritesPidFile()
    {
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);
        _runner.Setup(r => r.Start(ConfigFile, PidFile))
            .Callback(() => _runner.Setup(x => x.IsProcessAlive(88)).Returns(true))
            .Returns(88);

        var status = await CreateSut().StartAsync(CancellationToken.None);

        Assert.True(status.IsRunning);
        Assert.Equal(88, status.Pid);
        Assert.True(File.Exists(PidFile));
        Assert.Equal("88", (await File.ReadAllTextAsync(PidFile)).Trim());
    }

    [Fact]
    public async Task Kill_WhenRunning_StopsProcessAndRemovesPidFile()
    {
        await File.WriteAllTextAsync(PidFile, "55");
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(() => alive);
        _runner.Setup(r => r.TrySignal(55, false)).Callback(() => alive = false).Returns(true);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);

        var status = await CreateSut().KillAsync(CancellationToken.None);

        Assert.Equal("kill", status.Action);
        Assert.False(status.IsRunning);
        Assert.Null(status.Pid);
        Assert.Contains("pid file removed", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(PidFile));
        _runner.Verify(r => r.TrySignal(55, false), Times.Once);
    }

    [Fact]
    public async Task Kill_WhenStillAliveAfterKill_Throws()
    {
        await File.WriteAllTextAsync(PidFile, "55");
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(true);
        _runner.Setup(r => r.TrySignal(55, It.IsAny<bool>())).Returns(true);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().KillAsync(CancellationToken.None));

        Assert.Contains("kill failed verification", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(PidFile));
    }

    [Fact]
    public async Task Kill_WhenMatchingProcessRemains_Throws()
    {
        await File.WriteAllTextAsync(PidFile, "55");
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(() => alive);
        _runner.Setup(r => r.TrySignal(55, false)).Callback(() => alive = false).Returns(true);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([
            new OpenVpnRunningProcess(77, $"openvpn --config {ConfigFile}")
        ]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSut().KillAsync(CancellationToken.None));

        Assert.Contains("matching openvpn process remains", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restart_KillsThenStarts_WithPidFile()
    {
        await File.WriteAllTextAsync(PidFile, "11");
        var oldAlive = true;
        _runner.Setup(r => r.IsProcessAlive(11)).Returns(() => oldAlive);
        _runner.Setup(r => r.TrySignal(11, false)).Callback(() => oldAlive = false).Returns(true);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.Start(ConfigFile, PidFile))
            .Callback<string, string>((_, pidFile) =>
            {
                File.WriteAllText(pidFile, "22");
                _runner.Setup(x => x.IsProcessAlive(22)).Returns(true);
            })
            .Returns(22);

        var status = await CreateSut().RestartAsync(CancellationToken.None);

        Assert.Equal("restart", status.Action);
        Assert.True(status.IsRunning);
        Assert.Equal(22, status.Pid);
        Assert.Equal("22", (await File.ReadAllTextAsync(PidFile)).Trim());
        _runner.Verify(r => r.TrySignal(11, false), Times.Once);
        _runner.Verify(r => r.Start(ConfigFile, PidFile), Times.Once);
    }

    [Fact]
    public async Task Kill_WhenAnotherOperationHoldsGate_ThrowsBusy()
    {
        await File.WriteAllTextAsync(PidFile, "55");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(() => alive);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.TrySignal(55, false)).Returns(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            alive = false;
            return true;
        });

        var sut = CreateSut();
        var first = Task.Run(() => sut.KillAsync(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.KillAsync(CancellationToken.None));
        Assert.Equal(OpenVpnProcessService.BusyMessage, busy.Message);

        release.TrySetResult();
        var status = await first;
        Assert.False(status.IsRunning);
        Assert.Contains("stopped", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_WhileKillInProgress_ReportsOperationSnapshot()
    {
        await File.WriteAllTextAsync(PidFile, "55");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alive = true;
        _runner.Setup(r => r.IsProcessAlive(55)).Returns(() => alive);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.TrySignal(55, false)).Returns(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            alive = false;
            return true;
        });

        var sut = CreateSut();
        var kill = Task.Run(() => sut.KillAsync(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var status = await sut.GetStatusAsync(CancellationToken.None);
        Assert.True(status.OperationInProgress);
        Assert.Equal("kill", status.CurrentOperation);
        Assert.Equal(OpenVpnProcessService.PhaseStopping, status.Phase);
        Assert.Contains("in progress", status.Message, StringComparison.OrdinalIgnoreCase);

        release.TrySetResult();
        await kill;

        var after = await sut.GetStatusAsync(CancellationToken.None);
        Assert.False(after.OperationInProgress);
        Assert.Equal(OpenVpnProcessService.PhaseStopped, after.Phase);
        Assert.Equal("kill", after.LastCompletedOperation);
    }

    [Fact]
    public async Task Start_WhenConfigMissing_Throws()
    {
        File.Delete(ConfigFile);
        _runner.Setup(r => r.FindOpenVpnProcesses()).Returns([]);
        _runner.Setup(r => r.IsProcessAlive(It.IsAny<int>())).Returns(false);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            CreateSut().StartAsync(CancellationToken.None));
    }
}
