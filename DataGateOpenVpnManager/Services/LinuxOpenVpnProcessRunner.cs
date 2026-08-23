using System.Diagnostics;
using System.Text;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Services;

public sealed class LinuxOpenVpnProcessRunner(ILogger<LinuxOpenVpnProcessRunner> logger) : IOpenVpnProcessRunner
{
    public IReadOnlyList<OpenVpnRunningProcess> FindOpenVpnProcesses()
    {
        var result = new List<OpenVpnRunningProcess>();
        if (!Directory.Exists("/proc"))
            return result;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, out var pid) || pid <= 1)
                continue;

            try
            {
                var cmdlinePath = Path.Combine(dir, "cmdline");
                if (!File.Exists(cmdlinePath))
                    continue;

                var raw = File.ReadAllBytes(cmdlinePath);
                if (raw.Length == 0)
                    continue;

                var cmdline = Encoding.UTF8.GetString(raw).Replace('\0', ' ').Trim();
                if (string.IsNullOrWhiteSpace(cmdline))
                    continue;

                if (!cmdline.Contains("openvpn", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!cmdline.Contains("--config", StringComparison.OrdinalIgnoreCase)
                    && !cmdline.Contains("server.conf", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (cmdline.Contains("--genkey", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new OpenVpnRunningProcess(pid, cmdline));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    public bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public int Start(string configPath, string pidFilePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "openvpn",
            Arguments = $"--config \"{configPath}\" --writepid \"{pidFilePath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(configPath) ?? "/"
        };

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException("Failed to start openvpn process.");
        logger.LogInformation(
            "Started openvpn pid={Pid} config={Config} pidFile={PidFile}",
            process.Id, configPath, pidFilePath);

        for (var i = 0; i < 20; i++)
        {
            Thread.Sleep(50);
            if (TryReadPidFile(pidFilePath, out var written) && written > 0)
                return written;
        }

        return process.Id;
    }

    public bool TrySignal(int pid, bool force)
    {
        if (pid <= 0)
            return false;
        if (!IsProcessAlive(pid))
            return false;

        var signal = force ? "-KILL" : "-TERM";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"{signal} {pid}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var kill = Process.Start(psi);
            kill?.WaitForExit(2000);
            logger.LogInformation(
                "Signalled openvpn pid={Pid} signal={Signal} killExit={Exit}",
                pid, signal, kill?.ExitCode);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to signal openvpn pid={Pid}", pid);
            return false;
        }
    }

    internal static bool TryReadPidFile(string pidFilePath, out int pid)
    {
        pid = 0;
        try
        {
            if (!File.Exists(pidFilePath))
                return false;
            return int.TryParse(File.ReadAllText(pidFilePath).Trim(), out pid) && pid > 0;
        }
        catch
        {
            return false;
        }
    }
}
