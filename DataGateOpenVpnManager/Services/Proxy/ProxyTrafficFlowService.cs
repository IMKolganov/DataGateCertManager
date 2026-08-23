using System.Collections.Concurrent;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;

namespace DataGateOpenVpnManager.Services.Proxy;

public sealed class ProxyTrafficFlowService : IProxyTrafficFlowService
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, FlowConnectionState> _connections = new();
    private readonly ConcurrentQueue<ProxyTrafficFlowUpdate> _terminalUpdates = new();

    public void RegisterConnection(ActiveProxyConnection connection, ProxyConnectionIdentity? identity = null)
    {
        var connectedAt = connection.ConnectedAtUtc == default ? DateTime.UtcNow : connection.ConnectedAtUtc;
        var state = new FlowConnectionState(
            connection.ConnectionId,
            connection.Protocol,
            connection.RealClientIp,
            connection.RealClientPort,
            identity?.ClientRef,
            identity?.UserId,
            identity?.Username,
            identity?.Email,
            identity?.UserAgent,
            connection.LocalProxyIp,
            connection.LocalProxyPort,
            connection.TargetIp,
            connection.TargetPort,
            connectedAt);

        _connections[connection.ConnectionId] = state;
    }

    public IProxyFlowCounter? GetCounter(string connectionId) =>
        _connections.TryGetValue(connectionId, out var state) ? state : null;

    public ProxyTrafficFlowUpdate? UnregisterConnection(string connectionId, DateTime? disconnectedAtUtc = null)
    {
        if (!_connections.TryRemove(connectionId, out var state))
            return null;

        var emittedAt = disconnectedAtUtc ?? DateTime.UtcNow;
        var (c2sTotal, s2cTotal, c2sDelta, s2cDelta, lastActivity) = state.SnapshotAndClearDeltas();
        var terminal = new ProxyTrafficFlowUpdate
        {
            ConnectionId = state.ConnectionId,
            Protocol = state.Protocol,
            State = "disconnected",
            IsConnected = false,
            IsIdle = true,
            RealClientIp = state.RealClientIp,
            RealClientPort = state.RealClientPort,
            ClientRef = state.ClientRef,
            UserId = state.UserId,
            Username = state.Username,
            Email = state.Email,
            LocalProxyIp = state.LocalProxyIp,
            LocalProxyPort = state.LocalProxyPort,
            TargetIp = state.TargetIp,
            TargetPort = state.TargetPort,
            ClientToServerBytesTotal = c2sTotal,
            ServerToClientBytesTotal = s2cTotal,
            ClientToServerBytesDelta = c2sDelta,
            ServerToClientBytesDelta = s2cDelta,
            ConnectedAtUtc = state.ConnectedAtUtc,
            LastActivityAtUtc = lastActivity,
            EmittedAtUtc = emittedAt
        };

        _terminalUpdates.Enqueue(terminal);
        return terminal;
    }

    public bool TryGetTotals(string connectionId, out long clientToServerBytesTotal, out long serverToClientBytesTotal)
    {
        clientToServerBytesTotal = 0;
        serverToClientBytesTotal = 0;
        if (!_connections.TryGetValue(connectionId, out var state))
            return false;

        clientToServerBytesTotal = state.ClientToServerBytesTotal;
        serverToClientBytesTotal = state.ServerToClientBytesTotal;
        return true;
    }

    public void RegisterConnectFailed(
        string connectionId,
        ProxyConnectionProtocol protocol,
        string? realClientIp,
        int realClientPort,
        ProxyConnectionIdentity? identity,
        string targetIp,
        int targetPort,
        string? errorMessage,
        DateTime? failedAtUtc = null)
    {
        var at = failedAtUtc ?? DateTime.UtcNow;
        _terminalUpdates.Enqueue(new ProxyTrafficFlowUpdate
        {
            ConnectionId = connectionId,
            Protocol = protocol,
            State = "failed",
            IsConnected = false,
            IsIdle = true,
            RealClientIp = realClientIp,
            RealClientPort = realClientPort,
            ClientRef = identity?.ClientRef,
            UserId = identity?.UserId,
            Username = identity?.Username,
            Email = identity?.Email,
            LocalProxyIp = null,
            LocalProxyPort = 0,
            TargetIp = targetIp,
            TargetPort = targetPort,
            ClientToServerBytesTotal = 0,
            ServerToClientBytesTotal = 0,
            ClientToServerBytesDelta = 0,
            ServerToClientBytesDelta = 0,
            ConnectedAtUtc = at,
            LastActivityAtUtc = at,
            EmittedAtUtc = at,
            ErrorMessage = errorMessage
        });
    }

    public void RecordTraffic(
        string connectionId,
        ProxyTrafficFlowDirection direction,
        int bytes,
        DateTime? occurredAtUtc = null)
    {
        if (bytes <= 0)
            return;
        if (!_connections.TryGetValue(connectionId, out var state))
            return;

        state.Add(direction, bytes, occurredAtUtc);
    }

    public IReadOnlyCollection<ProxyTrafficFlowUpdate> BuildBatch(DateTime emittedAtUtc)
    {
        var result = new List<ProxyTrafficFlowUpdate>(_connections.Count + _terminalUpdates.Count);
        foreach (var state in _connections.Values)
        {
            var (c2sTotal, s2cTotal, c2sDelta, s2cDelta, lastActivity) = state.SnapshotAndClearDeltas();
            var isIdle = emittedAtUtc - lastActivity >= IdleThreshold;
            result.Add(new ProxyTrafficFlowUpdate
            {
                ConnectionId = state.ConnectionId,
                Protocol = state.Protocol,
                State = "connected",
                IsConnected = true,
                IsIdle = isIdle,
                RealClientIp = state.RealClientIp,
                RealClientPort = state.RealClientPort,
                ClientRef = state.ClientRef,
                UserId = state.UserId,
                Username = state.Username,
                Email = state.Email,
                LocalProxyIp = state.LocalProxyIp,
                LocalProxyPort = state.LocalProxyPort,
                TargetIp = state.TargetIp,
                TargetPort = state.TargetPort,
                ClientToServerBytesTotal = c2sTotal,
                ServerToClientBytesTotal = s2cTotal,
                ClientToServerBytesDelta = c2sDelta,
                ServerToClientBytesDelta = s2cDelta,
                ConnectedAtUtc = state.ConnectedAtUtc,
                LastActivityAtUtc = lastActivity,
                EmittedAtUtc = emittedAtUtc
            });
        }

        while (_terminalUpdates.TryDequeue(out var terminal))
            result.Add(terminal);

        return result;
    }

    public bool TryGetIdentityByLocalProxy(int localProxyPort, string? host, out ProxyTrafficIdentitySnapshot? identity)
    {
        identity = null;
        if (localProxyPort is < 1 or > 65535)
            return false;

        var needle = ActiveProxyConnectionService.NormalizeHost(host);
        foreach (var state in _connections.Values)
        {
            if (state.LocalProxyPort != localProxyPort)
                continue;
            if (!HostsEqual(state.LocalProxyIp, needle))
                continue;

            identity = new ProxyTrafficIdentitySnapshot
            {
                ConnectionId = state.ConnectionId,
                ClientRef = state.ClientRef,
                UserId = state.UserId,
                Username = state.Username,
                Email = state.Email,
                UserAgent = state.UserAgent,
                RealClientIp = state.RealClientIp,
                RealClientPort = state.RealClientPort,
                LocalProxyIp = state.LocalProxyIp,
                LocalProxyPort = state.LocalProxyPort
            };
            return true;
        }

        return false;
    }

    private static bool HostsEqual(string? localProxyIp, string needleNormalized) =>
        string.Equals(ActiveProxyConnectionService.NormalizeHost(localProxyIp), needleNormalized,
            StringComparison.OrdinalIgnoreCase);

    private sealed class FlowConnectionState : IProxyFlowCounter
    {
        private long _c2sTotal;
        private long _s2cTotal;
        private long _c2sDelta;
        private long _s2cDelta;
        private long _lastActivityUtcTicks;

        public FlowConnectionState(
            string connectionId,
            ProxyConnectionProtocol protocol,
            string? realClientIp,
            int realClientPort,
            string? clientRef,
            string? userId,
            string? username,
            string? email,
            string? userAgent,
            string? localProxyIp,
            int localProxyPort,
            string? targetIp,
            int targetPort,
            DateTime connectedAtUtc)
        {
            ConnectionId = connectionId;
            Protocol = protocol;
            RealClientIp = realClientIp;
            RealClientPort = realClientPort;
            ClientRef = clientRef;
            UserId = userId;
            Username = username;
            Email = email;
            UserAgent = userAgent;
            LocalProxyIp = localProxyIp;
            LocalProxyPort = localProxyPort;
            TargetIp = targetIp;
            TargetPort = targetPort;
            ConnectedAtUtc = connectedAtUtc;
            _lastActivityUtcTicks = connectedAtUtc.Ticks;
        }

        public string ConnectionId { get; }
        public ProxyConnectionProtocol Protocol { get; }
        public string? RealClientIp { get; }
        public int RealClientPort { get; }
        public string? ClientRef { get; }
        public string? UserId { get; }
        public string? Username { get; }
        public string? Email { get; }
        public string? UserAgent { get; }
        public string? LocalProxyIp { get; }
        public int LocalProxyPort { get; }
        public string? TargetIp { get; }
        public int TargetPort { get; }
        public DateTime ConnectedAtUtc { get; }

        public long ClientToServerBytesTotal => Interlocked.Read(ref _c2sTotal);
        public long ServerToClientBytesTotal => Interlocked.Read(ref _s2cTotal);

        public void Add(ProxyTrafficFlowDirection direction, long bytes) =>
            Add(direction, bytes, occurredAtUtc: null);

        public void Add(ProxyTrafficFlowDirection direction, long bytes, DateTime? occurredAtUtc)
        {
            if (bytes <= 0)
                return;

            if (direction == ProxyTrafficFlowDirection.ClientToServer)
            {
                Interlocked.Add(ref _c2sTotal, bytes);
                Interlocked.Add(ref _c2sDelta, bytes);
            }
            else
            {
                Interlocked.Add(ref _s2cTotal, bytes);
                Interlocked.Add(ref _s2cDelta, bytes);
            }

            var ticks = (occurredAtUtc ?? DateTime.UtcNow).Ticks;
            // Keep the newest activity timestamp without a lock.
            long current;
            do
            {
                current = Volatile.Read(ref _lastActivityUtcTicks);
                if (ticks <= current)
                    break;
            } while (Interlocked.CompareExchange(ref _lastActivityUtcTicks, ticks, current) != current);
        }

        public (long C2sTotal, long S2cTotal, long C2sDelta, long S2cDelta, DateTime LastActivity) SnapshotAndClearDeltas()
        {
            var c2sTotal = Interlocked.Read(ref _c2sTotal);
            var s2cTotal = Interlocked.Read(ref _s2cTotal);
            var c2sDelta = Interlocked.Exchange(ref _c2sDelta, 0);
            var s2cDelta = Interlocked.Exchange(ref _s2cDelta, 0);
            var last = new DateTime(Volatile.Read(ref _lastActivityUtcTicks), DateTimeKind.Utc);
            return (c2sTotal, s2cTotal, c2sDelta, s2cDelta, last);
        }
    }
}
