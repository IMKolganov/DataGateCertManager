namespace DataGateOpenVpnManager.Models;

public sealed class OpenVpnProcessStatusResponse
{
    public required string Action { get; init; }
    public required bool IsRunning { get; init; }
    public int? Pid { get; init; }
    public string? ConfigPath { get; init; }
    public string? PidFilePath { get; init; }
    public required string Message { get; init; }
}
