namespace DataGateOpenVpnManager.Services.Interfaces;

public sealed record OpenVpnRunningProcess(int Pid, string CommandLine);

/// <summary>OS-level helpers for finding / starting / signalling the OpenVPN daemon.</summary>
public interface IOpenVpnProcessRunner
{
    IReadOnlyList<OpenVpnRunningProcess> FindOpenVpnProcesses();
    bool IsProcessAlive(int pid);
    /// <summary>Starts openvpn and returns the OS pid.</summary>
    int Start(string configPath, string pidFilePath);
    bool TrySignal(int pid, bool force);
}
