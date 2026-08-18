using System.Net.Sockets;

namespace DataGateOpenVpnManager.Controllers;

/// <summary>
/// OpenVPN node <c>PROTO</c> is the source of truth. Clients that open WSS with the other
/// <c>mode</c> must not be bridged (TCP to a UDP daemon is connection refused in a loop).
/// </summary>
internal static class OpenVpnProxyProtocolGuard
{
    public const string TcpOnlyMessage = "This channel only supports TCP";
    public const string UdpOnlyMessage = "This channel only supports UDP";
    public const string TcpConnectFailedMessage = "TCP connect failed";
    public const string UdpConnectFailedMessage = "UDP connect failed";

    public static string NormalizeNodeProto(string? protoFromConfig)
    {
        var proto = protoFromConfig?.Trim().ToLowerInvariant();
        return proto is "udp" or "tcp" ? proto : "tcp";
    }

    public static string? NormalizeRequestedMode(string? modeFromQuery)
    {
        var mode = modeFromQuery?.Trim().ToLowerInvariant();
        return mode is "udp" or "tcp" ? mode : null;
    }

    public static string ResolveTunnelMode(string? modeFromQuery, string? protoFromConfig) =>
        NormalizeRequestedMode(modeFromQuery) ?? NormalizeNodeProto(protoFromConfig);

    public static bool TryGetMismatchMessage(string? modeFromQuery, string? protoFromConfig, out string message)
    {
        var nodeProto = NormalizeNodeProto(protoFromConfig);
        var requested = NormalizeRequestedMode(modeFromQuery);
        if (requested is null || requested == nodeProto)
        {
            message = string.Empty;
            return false;
        }

        message = SupportedOnlyMessage(nodeProto);
        return true;
    }

    public static string SupportedOnlyMessage(string nodeProto) =>
        nodeProto == "udp" ? UdpOnlyMessage : TcpOnlyMessage;

    public static bool IsConnectRefused(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                socket.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
                return true;
        }

        return false;
    }

    public static string ClientMessageForConnectFailure(string attemptedMode, string nodeProto, Exception exception)
    {
        if (IsConnectRefused(exception) && attemptedMode != nodeProto)
            return SupportedOnlyMessage(nodeProto);

        return attemptedMode == "udp" ? UdpConnectFailedMessage : TcpConnectFailedMessage;
    }

    public static bool IsProtocolMismatchConnectFailure(string attemptedMode, string nodeProto, Exception exception) =>
        IsConnectRefused(exception) && attemptedMode != nodeProto;
}
