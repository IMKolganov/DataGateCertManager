using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OpenVpnProcess.Responses;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Services;

public sealed class OpenVpnProcessService(
    IConfiguration config,
    IOpenVpnProcessRunner runner,
    ILogger<OpenVpnProcessService> logger) : IOpenVpnProcessService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object OpLock = new();
    private static readonly TimeSpan StartVerifyTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopVerifyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopForceVerifyTimeout = TimeSpan.FromSeconds(2);

    internal const string BusyMessage =
        "OpenVPN process operation already in progress. Please wait and try again.";

    internal const string PhaseIdle = "idle";
    internal const string PhaseRunning = "running";
    internal const string PhaseStopped = "stopped";
    internal const string PhaseStarting = "starting";
    internal const string PhaseStopping = "stopping";
    internal const string PhaseRestarting = "restarting";
    internal const string PhaseFailed = "failed";

    private static bool OpInProgress;
    private static string? CurrentOp;
    private static string CurrentPhase = PhaseIdle;
    private static DateTime? OpStartedAtUtc;
    private static string? LastCompletedOp;
    private static DateTime? LastCompletedAtUtc;
    private static string? LastError;

    /// <summary>Test-only: clears process-control gate/operation snapshot between unit tests.</summary>
    internal static void ResetStateForTests()
    {
        while (Gate.CurrentCount == 0)
            Gate.Release();
        lock (OpLock)
        {
            OpInProgress = false;
            CurrentOp = null;
            CurrentPhase = PhaseIdle;
            OpStartedAtUtc = null;
            LastCompletedOp = null;
            LastCompletedAtUtc = null;
            LastError = null;
        }
    }

    public Task<OpenVpnProcessStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        // Prefer a live operation snapshot so other clients (and this UI) see restart progress
        // without waiting behind the gate.
        if (Volatile.Read(ref OpInProgress))
        {
            var busyPid = ResolveRunningPidUnsafe();
            return Task.FromResult(BuildStatus(
                "status",
                busyPid,
                BuildInProgressMessage(busyPid)));
        }

        return GetStatusWhenIdleAsync(cancellationToken);
    }

    private async Task<OpenVpnProcessStatusResponse> GetStatusWhenIdleAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken))
        {
            var busyPid = ResolveRunningPidUnsafe();
            return BuildStatus("status", busyPid, BuildInProgressMessage(busyPid));
        }

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
        if (!await Gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(BusyMessage);

        BeginOperation("start", PhaseStarting);
        try
        {
            ClearStalePidFileIfNeeded();
            var existing = ResolveRunningPid();
            if (existing is { } pid)
            {
                EnsurePidFileMatches(pid);
                return CompleteOperation("start", pid, $"OpenVPN already running (pid {pid}).");
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
            return CompleteOperation("start", live, $"OpenVPN started (pid {live}, pid file present).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailOperation(ex.Message);
            throw;
        }
        finally
        {
            ClearInProgress();
            Gate.Release();
        }
    }

    public async Task<OpenVpnProcessStatusResponse> KillAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(BusyMessage);

        BeginOperation("kill", PhaseStopping);
        try
        {
            return await KillUnlockedAsync(cancellationToken, completeAs: "kill");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailOperation(ex.Message);
            throw;
        }
        finally
        {
            ClearInProgress();
            Gate.Release();
        }
    }

    public async Task<OpenVpnProcessStatusResponse> RestartAsync(CancellationToken cancellationToken)
    {
        if (!await Gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException(BusyMessage);

        BeginOperation("restart", PhaseRestarting);
        try
        {
            SetPhase(PhaseStopping);
            await KillUnlockedAsync(cancellationToken, completeAs: null);

            SetPhase(PhaseStarting);
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
            return CompleteOperation("restart", live, $"OpenVPN restarted (pid {live}, pid file present).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailOperation(ex.Message);
            throw;
        }
        finally
        {
            ClearInProgress();
            Gate.Release();
        }
    }

    /// <param name="completeAs">When set, records a completed operation (kill). Null when used as restart mid-step.</param>
    private async Task<OpenVpnProcessStatusResponse> KillUnlockedAsync(
        CancellationToken cancellationToken,
        string? completeAs)
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

            var already = BuildStatus("kill", null, "OpenVPN was not running.");
            if (completeAs is not null)
                return CompleteOperation(completeAs, null, already.Message);
            return already;
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

        var message = $"OpenVPN stopped (pid {pid} exited, pid file removed).";
        if (completeAs is not null)
            return CompleteOperation(completeAs, null, message);
        return BuildStatus("kill", null, message);
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

    /// <summary>Best-effort pid peek without clearing stale files (used when gate is busy).</summary>
    private int? ResolveRunningPidUnsafe()
    {
        var pidFile = PidFilePath();
        if (LinuxOpenVpnProcessRunner.TryReadPidFile(pidFile, out var fromFile) && runner.IsProcessAlive(fromFile))
            return fromFile;
        return FindMatchingOpenVpnProcesses().FirstOrDefault()?.Pid;
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

    private static void BeginOperation(string operation, string phase)
    {
        lock (OpLock)
        {
            OpInProgress = true;
            CurrentOp = operation;
            CurrentPhase = phase;
            OpStartedAtUtc = DateTime.UtcNow;
            LastError = null;
        }
    }

    private static void SetPhase(string phase)
    {
        lock (OpLock)
        {
            CurrentPhase = phase;
        }
    }

    private OpenVpnProcessStatusResponse CompleteOperation(string operation, int? pid, string message)
    {
        lock (OpLock)
        {
            OpInProgress = false;
            CurrentOp = null;
            OpStartedAtUtc = null;
            LastCompletedOp = operation;
            LastCompletedAtUtc = DateTime.UtcNow;
            LastError = null;
            CurrentPhase = pid is not null ? PhaseRunning : PhaseStopped;
        }

        return BuildStatus(operation, pid, message);
    }

    private static void FailOperation(string error)
    {
        lock (OpLock)
        {
            LastError = error;
            CurrentPhase = PhaseFailed;
            LastCompletedOp = CurrentOp;
            LastCompletedAtUtc = DateTime.UtcNow;
        }
    }

    private static void ClearInProgress()
    {
        lock (OpLock)
        {
            OpInProgress = false;
            CurrentOp = null;
            OpStartedAtUtc = null;
            if (CurrentPhase is PhaseStarting or PhaseStopping or PhaseRestarting)
                CurrentPhase = PhaseIdle;
        }
    }

    private string BuildInProgressMessage(int? pid)
    {
        string op;
        string phase;
        DateTime? started;
        lock (OpLock)
        {
            op = CurrentOp ?? "operation";
            phase = CurrentPhase;
            started = OpStartedAtUtc;
        }

        var since = started is { } t ? $" since {t:HH:mm:ss} UTC" : "";
        var daemon = pid is { } p ? $"daemon still up (pid {p})" : "daemon currently down";
        return $"OpenVPN {op} in progress ({phase}{since}); {daemon}.";
    }

    private OpenVpnProcessStatusResponse BuildStatus(string action, int? pid, string? message = null)
    {
        var running = pid is { } p && runner.IsProcessAlive(p);
        bool inProgress;
        string phase;
        string? currentOp;
        DateTime? opStarted;
        string? lastOp;
        DateTime? lastAt;
        string? lastError;
        lock (OpLock)
        {
            inProgress = OpInProgress;
            currentOp = CurrentOp;
            opStarted = OpStartedAtUtc;
            lastOp = LastCompletedOp;
            lastAt = LastCompletedAtUtc;
            lastError = LastError;
            phase = inProgress
                ? CurrentPhase
                : CurrentPhase is PhaseFailed
                    ? PhaseFailed
                    : running
                        ? PhaseRunning
                        : PhaseStopped;
        }

        return new OpenVpnProcessStatusResponse
        {
            Action = action,
            IsRunning = running,
            Pid = running ? pid : null,
            ConfigPath = ConfigPath(),
            PidFilePath = PidFilePath(),
            Message = message ?? (running ? "OpenVPN is running." : "OpenVPN is not running."),
            OperationInProgress = inProgress,
            Phase = phase,
            CurrentOperation = currentOp,
            OperationStartedAtUtc = opStarted,
            LastCompletedOperation = lastOp,
            LastCompletedAtUtc = lastAt,
            LastError = lastError
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
