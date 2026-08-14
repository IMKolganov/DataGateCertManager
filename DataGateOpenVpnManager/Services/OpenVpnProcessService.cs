using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OpenVpnProcess.Responses;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Services;

public sealed class OpenVpnProcessService(
    IConfiguration config,
    IOpenVpnProcessRunner runner,
    ILogger<OpenVpnProcessService> logger) : IOpenVpnProcessService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan StartVerifyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopVerifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopForceVerifyTimeout = TimeSpan.FromSeconds(2);

    public async Task<OpenVpnProcessStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            ClearStalePidFileIfNeeded();
            var pid = ResolveRunningPid();
            var pidFileExists = File.Exists(PidFilePath());
            return BuildStatus(
                "status",
                pid,
                pid is null
                    ? pidFileExists
                        ? "OpenVPN is not running (stale pid file was present)."
                        : "OpenVPN is not running."
                    : $"OpenVPN is running (pid {pid}, pid file {(pidFileExists ? "present" : "missing")}).");
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
            ClearStalePidFileIfNeeded();
            var existing = ResolveRunningPid();
            if (existing is { } pid)
            {
                EnsurePidFileMatches(pid);
                return BuildStatus("start", pid, $"OpenVPN already running (pid {pid}).");
            }

            var configPath = RequireConfigPath();
            var pidFile = PidFilePath();
            EnsurePidFileDirectory(pidFile);
            TryClearPidFile();

            var started = runner.Start(configPath, pidFile);
            logger.LogInformation("OpenVPN start requested; reportedPid={Pid}", started);

            var live = await WaitUntilRunningAsync(started, StartVerifyTimeout, cancellationToken);
            if (live is null)
            {
                throw new InvalidOperationException(
                    $"OpenVPN start failed verification: process is not running and/or pid file is missing " +
                    $"(expected pid file at '{pidFile}').");
            }

            EnsurePidFileMatches(live.Value);
            return BuildStatus("start", live, $"OpenVPN started (pid {live}, pid file present).");
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

            var configPath = RequireConfigPath();
            var pidFile = PidFilePath();
            EnsurePidFileDirectory(pidFile);
            TryClearPidFile();

            var started = runner.Start(configPath, pidFile);
            var live = await WaitUntilRunningAsync(started, StartVerifyTimeout, cancellationToken);
            if (live is null)
            {
                throw new InvalidOperationException(
                    $"OpenVPN restart killed the old process, but start failed verification " +
                    $"(process/pid file not confirmed at '{pidFile}').");
            }

            EnsurePidFileMatches(live.Value);
            return BuildStatus("restart", live, $"OpenVPN restarted (pid {live}, pid file present).");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<OpenVpnProcessStatusResponse> KillUnlockedAsync(CancellationToken cancellationToken)
    {
        ClearStalePidFileIfNeeded();
        var pid = ResolveRunningPid();
        if (pid is null)
        {
            TryClearPidFile();
            if (FindMatchingOpenVpnProcesses().Count > 0)
            {
                throw new InvalidOperationException(
                    "OpenVPN pid was unknown, but a matching openvpn process is still visible; refuse to claim stopped.");
            }

            return BuildStatus("kill", null, "OpenVPN was not running.");
        }

        logger.LogWarning("Killing OpenVPN pid={Pid} (clients will disconnect)", pid);
        runner.TrySignal(pid.Value, force: false);

        var stopped = await WaitUntilStoppedAsync(pid.Value, StopVerifyTimeout, cancellationToken);
        if (!stopped)
        {
            logger.LogWarning("OpenVPN pid={Pid} still alive after SIGTERM; sending SIGKILL", pid);
            runner.TrySignal(pid.Value, force: true);
            stopped = await WaitUntilStoppedAsync(pid.Value, StopForceVerifyTimeout, cancellationToken);
        }

        if (!stopped || runner.IsProcessAlive(pid.Value) || FindMatchingOpenVpnProcesses().Count > 0)
        {
            throw new InvalidOperationException(
                $"OpenVPN kill failed verification: process pid={pid} is still running " +
                $"or another matching openvpn process remains.");
        }

        TryClearPidFile();
        if (File.Exists(PidFilePath()))
        {
            throw new InvalidOperationException(
                $"OpenVPN process stopped, but pid file could not be removed: '{PidFilePath()}'.");
        }

        return BuildStatus("kill", null, $"OpenVPN stopped (pid {pid} exited, pid file removed).");
    }

    private async Task<int?> WaitUntilRunningAsync(int startedPid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow <= deadline)
        {
            EnsurePidFileForAliveProcess(startedPid);
            var live = ResolveRunningPid();
            if (live is { } pid
                && runner.IsProcessAlive(pid)
                && File.Exists(PidFilePath())
                && LinuxOpenVpnProcessRunner.TryReadPidFile(PidFilePath(), out var filePid)
                && filePid == pid)
            {
                return pid;
            }

            if (runner.IsProcessAlive(startedPid))
                EnsurePidFileForAliveProcess(startedPid);

            await Task.Delay(50, ct);
        }

        EnsurePidFileForAliveProcess(startedPid);
        var final = ResolveRunningPid();
        if (final is { } p
            && runner.IsProcessAlive(p)
            && File.Exists(PidFilePath())
            && LinuxOpenVpnProcessRunner.TryReadPidFile(PidFilePath(), out var written)
            && written == p)
        {
            return p;
        }

        return null;
    }

    private async Task<bool> WaitUntilStoppedAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow <= deadline)
        {
            if (!runner.IsProcessAlive(pid) && FindMatchingOpenVpnProcesses().Count == 0)
                return true;
            await Task.Delay(50, ct);
        }

        return !runner.IsProcessAlive(pid) && FindMatchingOpenVpnProcesses().Count == 0;
    }

    private void EnsurePidFileForAliveProcess(int pid)
    {
        if (pid <= 0 || !runner.IsProcessAlive(pid))
            return;

        var pidFile = PidFilePath();
        if (LinuxOpenVpnProcessRunner.TryReadPidFile(pidFile, out var existing) && existing == pid)
            return;

        try
        {
            EnsurePidFileDirectory(pidFile);
            File.WriteAllText(pidFile, pid.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write OpenVPN pid file {PidFile} for pid={Pid}", pidFile, pid);
        }
    }

    private void EnsurePidFileMatches(int pid)
    {
        EnsurePidFileForAliveProcess(pid);
        if (!File.Exists(PidFilePath())
            || !LinuxOpenVpnProcessRunner.TryReadPidFile(PidFilePath(), out var filePid)
            || filePid != pid
            || !runner.IsProcessAlive(pid))
        {
            throw new InvalidOperationException(
                $"OpenVPN verification failed for pid={pid}: process alive={runner.IsProcessAlive(pid)}, " +
                $"pid file exists={File.Exists(PidFilePath())}.");
        }
    }

    private void ClearStalePidFileIfNeeded()
    {
        var pidFile = PidFilePath();
        if (!LinuxOpenVpnProcessRunner.TryReadPidFile(pidFile, out var fromFile))
            return;
        if (runner.IsProcessAlive(fromFile))
            return;

        logger.LogInformation("Clearing stale OpenVPN pid file {PidFile} (dead pid={Pid})", pidFile, fromFile);
        TryClearPidFile();
    }

    private int? ResolveRunningPid()
    {
        var pidFile = PidFilePath();
        if (LinuxOpenVpnProcessRunner.TryReadPidFile(pidFile, out var fromFile) && runner.IsProcessAlive(fromFile))
            return fromFile;

        var matches = FindMatchingOpenVpnProcesses();
        return matches.FirstOrDefault()?.Pid;
    }

    private IReadOnlyList<OpenVpnRunningProcess> FindMatchingOpenVpnProcesses()
    {
        var configPath = ConfigPath();
        return runner.FindOpenVpnProcesses()
            .Where(p =>
                p.CommandLine.Contains(configPath, StringComparison.OrdinalIgnoreCase)
                || p.CommandLine.Contains("server.conf", StringComparison.OrdinalIgnoreCase))
            .ToList();
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
