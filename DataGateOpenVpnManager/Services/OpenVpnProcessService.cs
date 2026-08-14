using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OpenVpnProcess.Responses;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Services;

public sealed class OpenVpnProcessService(
    IConfiguration config,
    IOpenVpnProcessRunner runner,
    ILogger<OpenVpnProcessService> logger) : IOpenVpnProcessService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<OpenVpnProcessStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return BuildStatus("status", ResolveRunningPid());
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OpenVpnProcessStatusResponse> StartAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var existing = ResolveRunningPid();
            if (existing is { } pid)
            {
                return BuildStatus("start", pid, "OpenVPN already running.");
            }

            var configPath = RequireConfigPath();
            var pidFile = PidFilePath();
            EnsurePidFileDirectory(pidFile);

            var started = runner.Start(configPath, pidFile);
            logger.LogInformation("OpenVPN start requested; pid={Pid}", started);

            // Brief settle so status reflects reality.
            await Task.Delay(200, cancellationToken);
            var live = ResolveRunningPid() ?? (runner.IsProcessAlive(started) ? started : null);
            return BuildStatus(
                "start",
                live,
                live is null
                    ? "OpenVPN start was invoked but process is not running yet."
                    : "OpenVPN started.");
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OpenVpnProcessStatusResponse> KillAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await KillUnlockedAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<OpenVpnProcessStatusResponse> RestartAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await KillUnlockedAsync(cancellationToken);
            await Task.Delay(300, cancellationToken);

            var configPath = RequireConfigPath();
            var pidFile = PidFilePath();
            EnsurePidFileDirectory(pidFile);
            var started = runner.Start(configPath, pidFile);
            await Task.Delay(200, cancellationToken);
            var live = ResolveRunningPid() ?? (runner.IsProcessAlive(started) ? started : null);
            return BuildStatus(
                "restart",
                live,
                live is null
                    ? "OpenVPN restart completed kill, but process is not running yet."
                    : "OpenVPN restarted.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<OpenVpnProcessStatusResponse> KillUnlockedAsync(CancellationToken cancellationToken)
    {
        var pid = ResolveRunningPid();
        if (pid is null)
        {
            TryClearPidFile();
            return BuildStatus("kill", null, "OpenVPN was not running.");
        }

        logger.LogWarning("Killing OpenVPN pid={Pid} (clients will disconnect)", pid);
        runner.TrySignal(pid.Value, force: false);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && runner.IsProcessAlive(pid.Value))
        {
            await Task.Delay(100, cancellationToken);
        }

        if (runner.IsProcessAlive(pid.Value))
        {
            logger.LogWarning("OpenVPN pid={Pid} still alive after SIGTERM; sending SIGKILL", pid);
            runner.TrySignal(pid.Value, force: true);
            await Task.Delay(200, cancellationToken);
        }

        var stillUp = runner.IsProcessAlive(pid.Value);
        if (!stillUp)
            TryClearPidFile();

        return BuildStatus(
            "kill",
            stillUp ? pid : null,
            stillUp
                ? "OpenVPN kill signalled but process is still running."
                : "OpenVPN stopped.");
    }

    private int? ResolveRunningPid()
    {
        var pidFile = PidFilePath();
        if (LinuxOpenVpnProcessRunner.TryReadPidFile(pidFile, out var fromFile) && runner.IsProcessAlive(fromFile))
            return fromFile;

        var configPath = ConfigPath();
        var matches = runner.FindOpenVpnProcesses();
        var preferred = matches.FirstOrDefault(p =>
            p.CommandLine.Contains(configPath, StringComparison.OrdinalIgnoreCase)
            || p.CommandLine.Contains("server.conf", StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
            return preferred.Pid;

        return matches.FirstOrDefault()?.Pid;
    }

    private OpenVpnProcessStatusResponse BuildStatus(string action, int? pid, string? message = null)
    {
        var running = pid is { } p && runner.IsProcessAlive(p);
        return new OpenVpnProcessStatusResponse
        {
            Action = action,
            IsRunning = running,
            Pid = running ? pid : null,
            ConfigPath = ConfigPath(),
            PidFilePath = PidFilePath(),
            Message = message ?? (running ? "OpenVPN is running." : "OpenVPN is not running.")
        };
    }

    private string DataDir() =>
        config["DATA_DIR"]
        ?? Environment.GetEnvironmentVariable("DATA_DIR")
        ?? "/mnt";

    private string ConfigPath() => Path.Combine(DataDir(), "server.conf");

    private string RequireConfigPath()
    {
        var path = ConfigPath();
        if (!File.Exists(path))
            throw new FileNotFoundException($"OpenVPN config not found: {path}", path);
        return path;
    }

    private string PidFilePath() => Path.Combine(DataDir(), "openvpn.pid");

    private static void EnsurePidFileDirectory(string pidFile)
    {
        var dir = Path.GetDirectoryName(pidFile);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private void TryClearPidFile()
    {
        try
        {
            var path = PidFilePath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not clear OpenVPN pid file");
        }
    }
}
