using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Channels;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Requests;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Responses;
using DataGateMonitor.SharedModels.Responses;
using Microsoft.AspNetCore.Mvc;

namespace DataGateOpenVpnManager.Controllers;

[ApiController]
[Route("api/proxy")]
public class OpenVpnProxyController(
    IConfiguration config,
    ILogger<OpenVpnProxyController> logger,
    IActiveProxyConnectionService activeProxyConnections,
    IProxyConnectionHistoryService proxyConnectionHistory,
    IProxyTrafficFlowService proxyTrafficFlow,
    IProxyConnectionIdentityResolver identityResolver,
    IProxyByteDebugService proxyByteDebug,
    IProxyConnectionLifetimeService connectionLifetime,
    IProxySessionAuditService sessionAudit,
    ProxyBatchBufferPool batchBufferPool) : ControllerBase
{
    private const int WsSegmentSize = 64 * 1024;
    private const int UdpSocketBufferBytes = 4 * 1024 * 1024;
    private const int UdpSendQueueDepth = 4;

    /// <summary>
    /// Resolves the real WebSocket client address by the local ephemeral port of the socket
    /// that the proxy uses toward OpenVPN (127.0.0.1:vpnPort). The dashboard often only sees loopback.
    /// </summary>
    [HttpGet("client/by-local-port")]
    public ActionResult<ApiResponse<ProxyClientLookupResponse>> GetClientByLocalPort([FromQuery] GetProxyClientByLocalPortRequest request)
    {
        if (request.LocalPort is < 1 or > 65535)
            return BadRequest(ApiResponse<ProxyClientLookupResponse>.ErrorResponse(
                "Local port must be between 1 and 65535."));

        var host = string.IsNullOrWhiteSpace(request.Host) ? "localhost" : request.Host;
        var conn = activeProxyConnections.TryGetByLocalProxy(request.LocalPort, host);
        if (conn is null)
            return NotFound(ApiResponse<ProxyClientLookupResponse>.ErrorResponse(
                "No active proxy session for the given local port."));

        var hostNormalized = ActiveProxyConnectionService.NormalizeHost(host);
        return Ok(ApiResponse<ProxyClientLookupResponse>.SuccessResponse(
            MapToProxyClientLookupResponse(conn, hostNormalized)));
    }

    private static ProxyClientLookupResponse MapToProxyClientLookupResponse(ActiveProxyConnection c, string hostNormalized) =>
        new()
        {
            Host = hostNormalized,
            ConnectionId = c.ConnectionId,
            Protocol = c.Protocol,
            RealClientIp = c.RealClientIp,
            RealClientPort = c.RealClientPort,
            LocalProxyIp = c.LocalProxyIp,
            LocalProxyPort = c.LocalProxyPort,
            TargetIp = c.TargetIp,
            TargetPort = c.TargetPort,
            ConnectedAtUtc = c.ConnectedAtUtc
        };

    [HttpGet]
    public async Task Get([FromQuery] string? mode = null, [FromQuery] string? clientRef = null)
    {
        var portRaw = config["PORT"];
        if (!int.TryParse(portRaw, out var vpnPort) || vpnPort <= 0 || vpnPort > 65535)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await HttpContext.Response.WriteAsync("VPN port is not configured");
            return;
        }

        var nodeProto = OpenVpnProxyProtocolGuard.NormalizeNodeProto(config["PROTO"]);
        if (OpenVpnProxyProtocolGuard.TryGetMismatchMessage(mode, config["PROTO"], out var mismatchMessage))
        {
            var (mismatchClientIp, _) = GetHttpClientAddress();
            logger.LogWarning(
                "Proxy protocol mismatch. requested={Requested} supported={Supported} client={ClientIp}",
                mode, nodeProto, mismatchClientIp ?? "-");
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsync(mismatchMessage);
            return;
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsync("WebSocket request required");
            return;
        }

        const string targetHost = "127.0.0.1";
        var targetIp = IPAddress.Loopback;

        using var ws = await HttpContext.WebSockets.AcceptWebSocketAsync();

        var ct = HttpContext.RequestAborted;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var modeNorm = OpenVpnProxyProtocolGuard.ResolveTunnelMode(mode, config["PROTO"]);
        var identity = identityResolver.Resolve(HttpContext, clientRef);
        var connectionId = Guid.NewGuid().ToString("N");
        connectionLifetime.Register(connectionId, linkedCts);

        try
        {
            if (modeNorm == "udp")
                await HandleUdp(ws, targetIp, vpnPort, linkedCts.Token, logger, connectionId, identity);
            else
                await HandleTcp(ws, targetHost, vpnPort, linkedCts.Token, logger, connectionId, identity);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Proxy failed. mode={Mode} {Message}", modeNorm, e.Message);
        }
        finally
        {
            connectionLifetime.Unregister(connectionId);
        }

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "WebSocket close error. {Message}", e.Message);
        }
    }

    private async Task HandleTcp(
        WebSocket ws,
        string targetHost,
        int vpnPort,
        CancellationToken ct,
        ILogger logger,
        string connectionId,
        ProxyConnectionIdentity? identity)
    {
        using var tcp = new TcpClient();
        tcp.NoDelay = true;

        try
        {
            await tcp.ConnectAsync(targetHost, vpnPort, ct);
        }
        catch (Exception e)
        {
            var (clientIp, clientPort) = GetHttpClientAddress();
            var nodeProto = OpenVpnProxyProtocolGuard.NormalizeNodeProto(config["PROTO"]);
            var clientMessage = OpenVpnProxyProtocolGuard.ClientMessageForConnectFailure("tcp", nodeProto, e);
            var isMismatch = OpenVpnProxyProtocolGuard.IsProtocolMismatchConnectFailure("tcp", nodeProto, e);
            if (isMismatch)
            {
                logger.LogWarning(e,
                    "TCP connect rejected: channel is {Supported}. {Host}:{Port}. client={ClientIp}:{ClientPort}",
                    nodeProto, targetHost, vpnPort, clientIp ?? "-", clientPort);
            }
            else
            {
                logger.LogError(e,
                    "TCP connect failed. {Host}:{Port}. client={ClientIp}:{ClientPort}. {Message}",
                    targetHost, vpnPort, clientIp ?? "-", clientPort, e.Message);
            }

            RecordConnectFailed(connectionId, ProxyConnectionProtocol.Tcp, targetHost, vpnPort, clientMessage, identity);
            await TryCloseWs(ws, clientMessage, logger);
            return;
        }

        var localEp = (IPEndPoint)tcp.Client.LocalEndPoint!;
        var remoteEp = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        RegisterActiveConnection(connectionId, ProxyConnectionProtocol.Tcp, localEp, remoteEp, identity);

        try
        {
            await using var tcpStream = tcp.GetStream();
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pumpCt = pumpCts.Token;

            var counter = proxyTrafficFlow.GetCounter(connectionId);
            var wsToTcp = PumpWebSocketToTcp(ws, tcpStream, pumpCt, logger, counter);
            var tcpToWs = PumpTcpToWebSocket(ws, tcpStream, pumpCt, logger, counter);

            await Task.WhenAny(wsToTcp, tcpToWs);
            await pumpCts.CancelAsync();

            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await Task.WhenAll(wsToTcp, tcpToWs).WaitAsync(drain.Token);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "TCP pump drain did not finish in time");
            }
        }
        finally
        {
            UnregisterConnection(connectionId);
        }
    }

    private async Task HandleUdp(
        WebSocket ws,
        IPAddress targetIp,
        int vpnPort,
        CancellationToken ct,
        ILogger logger,
        string connectionId,
        ProxyConnectionIdentity? identity)
    {
        var remote = new IPEndPoint(targetIp, vpnPort);
        Socket? socket = null;
        try
        {
            socket = CreateVpnUdpSocket(remote, logger);
            logger.LogInformation("UDP proxy started. remote={Remote} local={Local}", remote, socket.LocalEndPoint);
        }
        catch (Exception e)
        {
            socket?.Dispose();
            var nodeProto = OpenVpnProxyProtocolGuard.NormalizeNodeProto(config["PROTO"]);
            var clientMessage = OpenVpnProxyProtocolGuard.ClientMessageForConnectFailure("udp", nodeProto, e);
            var isMismatch = OpenVpnProxyProtocolGuard.IsProtocolMismatchConnectFailure("udp", nodeProto, e);
            if (isMismatch)
            {
                logger.LogWarning(e,
                    "UDP connect rejected: channel is {Supported}. {Host}:{Port}. {Message}",
                    nodeProto, remote.Address, remote.Port, e.Message);
            }
            else
            {
                logger.LogError(e, "UDP connect failed. {Host}:{Port}. {Message}", remote.Address, remote.Port, e.Message);
            }

            RecordConnectFailed(connectionId, ProxyConnectionProtocol.Udp, remote.Address.ToString(), remote.Port, clientMessage, identity);
            if (ws.State == WebSocketState.Open)
                await SafeCloseWs(ws, WebSocketCloseStatus.InternalServerError, clientMessage);
            return;
        }

        using (socket)
        {
            var localEp = (IPEndPoint)socket.LocalEndPoint!;
            RegisterActiveConnection(connectionId, ProxyConnectionProtocol.Udp, localEp, remote, identity);
            var counter = proxyTrafficFlow.GetCounter(connectionId);

            try
            {
                using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pumpCt = pumpCts.Token;

                var wsToUdp = PumpWebSocketToUdp(ws, socket, pumpCt, logger, counter);
                var udpToWs = PumpUdpToWebSocket(ws, socket, pumpCt, logger, counter, batchBufferPool);

                await Task.WhenAny(wsToUdp, udpToWs);
                await pumpCts.CancelAsync();

                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await Task.WhenAll(wsToUdp, udpToWs).WaitAsync(drain.Token);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "UDP pump drain did not finish in time");
                }
            }
            finally
            {
                UnregisterConnection(connectionId);
            }
        }

        if (ws.State == WebSocketState.Open)
            await SafeCloseWs(ws, WebSocketCloseStatus.NormalClosure, "Closing");
    }

    private static Socket CreateVpnUdpSocket(IPEndPoint remote, ILogger logger)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            try
            {
                socket.ReceiveBufferSize = UdpSocketBufferBytes;
                socket.SendBufferSize = UdpSocketBufferBytes;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Could not enlarge UDP socket buffers");
            }

            // Kernel silently clamps to net.core.rmem_max — log so ops can see it.
            if (socket.ReceiveBufferSize < UdpSocketBufferBytes)
            {
                logger.LogWarning(
                    "UDP SO_RCVBUF clamped to {Actual} (requested {Requested}); raise net.core.rmem_max in the container netns",
                    socket.ReceiveBufferSize, UdpSocketBufferBytes);
            }

            socket.Connect(remote);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// WS→UDP: framed binary messages <c>[u16_be len][payload]...</c>.
    /// </summary>
    private static async Task PumpWebSocketToUdp(
        WebSocket ws,
        Socket socket,
        CancellationToken ct,
        ILogger logger,
        IProxyFlowCounter? counter)
    {
        var segment = ArrayPool<byte>.Shared.Rent(WsSegmentSize);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var (messageType, payload, payloadLen, ownsPayload) =
                    await ReceiveWholeWsMessageRented(ws, segment, ct);
                try
                {
                    if (messageType == WebSocketMessageType.Close)
                        break;

                    if (messageType != WebSocketMessageType.Binary || payloadLen == 0)
                        continue;

                    var off = 0;
                    long batchBytes = 0;
                    while (off + 2 <= payloadLen)
                    {
                        var next = UdpWsFraming.TryParseNextFrame(payload.AsSpan(0, payloadLen), off, out var frame);
                        if (next < 0)
                        {
                            logger.LogWarning(
                                "Invalid framed UDP message. off={Off} total={Total}",
                                off, payloadLen);
                            break;
                        }

                        var frameLen = frame.Length;
                        await socket.SendAsync(payload.AsMemory(off + 2, frameLen), SocketFlags.None, ct);
                        batchBytes += frameLen;
                        off = next;
                    }

                    if (batchBytes > 0)
                        counter?.Add(ProxyTrafficFlowDirection.ClientToServer, batchBytes);
                }
                finally
                {
                    if (ownsPayload)
                        ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "WS->UDP pump error. {Message}", e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(segment);
        }
    }

    /// <summary>
    /// UDP→WS with producer/sender split so batching continues while <see cref="WebSocket.SendAsync"/> is in flight.
    /// </summary>
    private static async Task PumpUdpToWebSocket(
        WebSocket ws,
        Socket socket,
        CancellationToken ct,
        ILogger logger,
        IProxyFlowCounter? counter,
        ProxyBatchBufferPool pool)
    {
        var channel = Channel.CreateBounded<UdpWsBatch>(
            new BoundedChannelOptions(UdpSendQueueDepth)
            {
                SingleReader = true,
                SingleWriter = true,
                // Drop newest under backpressure (UDP-like); TryWrite false → return buffer.
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false
            });

        var producer = ProduceUdpBatchesAsync(socket, channel.Writer, pool, ct, logger);
        var sender = SendUdpBatchesAsync(ws, channel.Reader, pool, counter, ct, logger);

        await Task.WhenAny(producer, sender);
        channel.Writer.TryComplete();
        try
        {
            await Task.WhenAll(producer, sender);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "UDP->WS pump error. {Message}", e.Message);
        }
    }

    private readonly record struct UdpWsBatch(byte[] Buffer, int Length, long PayloadBytes);

    private static async Task ProduceUdpBatchesAsync(
        Socket socket,
        ChannelWriter<UdpWsBatch> writer,
        ProxyBatchBufferPool pool,
        CancellationToken ct,
        ILogger logger)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(UdpWsFraming.MaxPayloadLength);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = pool.Rent();
                var offset = 0;
                long payloadBytes = 0;

                try
                {
                    bool moreQueued;
                    do
                    {
                        var receiveVt = socket.ReceiveAsync(scratch.AsMemory(0, UdpWsFraming.MaxPayloadLength), SocketFlags.None, ct);
                        moreQueued = receiveVt.IsCompleted;

                        int n;
                        try
                        {
                            n = await receiveVt;
                        }
                        catch (SocketException ex) when (IsTransientUdpError(ex.SocketErrorCode))
                        {
                            moreQueued = false;
                            continue;
                        }

                        if (n is <= 0 or > UdpWsFraming.MaxPayloadLength)
                        {
                            moreQueued = false;
                            continue;
                        }

                        if (!UdpWsFraming.CanAppendFrame(offset, n, ProxyBatchBufferPool.BufferSize, UdpWsFraming.BatchTargetBytes)
                            && offset > 0)
                        {
                            if (!writer.TryWrite(new UdpWsBatch(batch, offset, payloadBytes)))
                                pool.Return(batch);

                            batch = pool.Rent();
                            offset = 0;
                            payloadBytes = 0;
                        }

                        if (offset + 2 + n > ProxyBatchBufferPool.BufferSize)
                        {
                            // Should be unreachable for n <= MaxPayloadLength once capacity >= MaxFrameBytes.
                            logger.LogWarning(
                                "UDP datagram {Size}B dropped: exceeds batch buffer capacity {Capacity}",
                                n, ProxyBatchBufferPool.BufferSize);
                            moreQueued = false;
                            continue;
                        }

                        offset += UdpWsFraming.WriteFrame(batch.AsSpan(offset), scratch.AsSpan(0, n));
                        payloadBytes += n;
                    }
                    while (moreQueued
                           && offset < UdpWsFraming.BatchTargetBytes
                           && offset + 2 + UdpWsFraming.MaxPayloadLength <= ProxyBatchBufferPool.BufferSize
                           && !ct.IsCancellationRequested);

                    if (offset == 0)
                    {
                        pool.Return(batch);
                        continue;
                    }

                    if (!writer.TryWrite(new UdpWsBatch(batch, offset, payloadBytes)))
                        pool.Return(batch);
                }
                catch (OperationCanceledException)
                {
                    pool.Return(batch);
                    break;
                }
                catch (Exception e)
                {
                    pool.Return(batch);
                    logger.LogDebug(e, "UDP receive/batch error. {Message}", e.Message);
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            writer.TryComplete();
        }
    }

    private static async Task SendUdpBatchesAsync(
        WebSocket ws,
        ChannelReader<UdpWsBatch> reader,
        ProxyBatchBufferPool pool,
        IProxyFlowCounter? counter,
        CancellationToken ct,
        ILogger logger)
    {
        try
        {
            await foreach (var batch in reader.ReadAllAsync(ct))
            {
                try
                {
                    if (ws.State != WebSocketState.Open)
                        break;

                    await ws.SendAsync(
                        batch.Buffer.AsMemory(0, batch.Length),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken: ct);
                    if (batch.PayloadBytes > 0)
                        counter?.Add(ProxyTrafficFlowDirection.ServerToClient, batch.PayloadBytes);
                }
                finally
                {
                    pool.Return(batch.Buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "UDP->WS send error. {Message}", e.Message);
        }
    }

    private static bool IsTransientUdpError(SocketError error) =>
        error is SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.NoBufferSpaceAvailable
            or SocketError.MessageSize
            or SocketError.Interrupted
            or SocketError.WouldBlock;

    private void RegisterActiveConnection(
        string connectionId,
        ProxyConnectionProtocol protocol,
        IPEndPoint localEp,
        IPEndPoint remoteTargetEp,
        ProxyConnectionIdentity? identity)
    {
        var (clientIp, clientPort) = GetHttpClientAddress();
        var connection = new ActiveProxyConnection
        {
            ConnectionId = connectionId,
            Protocol = protocol,
            RealClientIp = clientIp,
            RealClientPort = clientPort,
            LocalProxyIp = localEp.Address.ToString(),
            LocalProxyPort = localEp.Port,
            TargetIp = remoteTargetEp.Address.ToString(),
            TargetPort = remoteTargetEp.Port,
            ConnectedAtUtc = DateTime.UtcNow
        };

        activeProxyConnections.Add(connection);
        proxyTrafficFlow.RegisterConnection(connection, identity);

        var connectDetails = ProxyAuditDetails.ForConnection(connection);
        if (identity?.ClientRef is not null)
            connectDetails["clientRef"] = identity.ClientRef;
        if (identity?.UserAgent is not null)
            connectDetails["userAgent"] = identity.UserAgent;
        sessionAudit.Record(new ProxySessionAuditEntry
        {
            AtUtc = DateTime.UtcNow,
            Event = "proxy.connected",
            ConnectionId = connectionId,
            Decision = "ok",
            Reason = protocol.ToString(),
            Details = connectDetails
        });

        proxyConnectionHistory.Add(new ProxyConnectionHistoryItem
        {
            ConnectionId = connectionId,
            Protocol = protocol,
            RealClientIp = clientIp,
            RealClientPort = clientPort,
            LocalProxyIp = localEp.Address.ToString(),
            LocalProxyPort = localEp.Port,
            TargetIp = remoteTargetEp.Address.ToString(),
            TargetPort = remoteTargetEp.Port,
            EventType = ProxyConnectionEventType.Connected,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private void UnregisterConnection(string connectionId)
    {
        ActiveProxyConnection? conn = null;
        if (activeProxyConnections.TryGet(connectionId, out var existing))
            conn = existing;

        activeProxyConnections.Remove(connectionId);
        var terminal = proxyTrafficFlow.UnregisterConnection(connectionId);
        if (terminal is not null)
        {
            proxyByteDebug.ReportDisconnect(terminal);
            var disconnectDetails = new Dictionary<string, string>
            {
                ["proxyC2S"] = terminal.ClientToServerBytesTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["proxyS2C"] = terminal.ServerToClientBytesTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["local"] = $"{terminal.LocalProxyIp}:{terminal.LocalProxyPort}"
            };
            sessionAudit.Record(new ProxySessionAuditEntry
            {
                AtUtc = DateTime.UtcNow,
                Event = "proxy.disconnected",
                ConnectionId = connectionId,
                Decision = "closed",
                Reason = "pump finished",
                Details = disconnectDetails
            });
        }

        if (conn is null)
            return;

        proxyConnectionHistory.Add(new ProxyConnectionHistoryItem
        {
            ConnectionId = conn.ConnectionId,
            Protocol = conn.Protocol,
            RealClientIp = conn.RealClientIp,
            RealClientPort = conn.RealClientPort,
            LocalProxyIp = conn.LocalProxyIp,
            LocalProxyPort = conn.LocalProxyPort,
            TargetIp = conn.TargetIp,
            TargetPort = conn.TargetPort,
            EventType = ProxyConnectionEventType.Disconnected,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    private void RecordConnectFailed(
        string connectionId,
        ProxyConnectionProtocol protocol,
        string targetIp,
        int targetPort,
        string errorMessage,
        ProxyConnectionIdentity? identity)
    {
        var (clientIp, clientPort) = GetHttpClientAddress();
        proxyTrafficFlow.RegisterConnectFailed(connectionId, protocol, clientIp, clientPort, identity, targetIp, targetPort, errorMessage);
        proxyConnectionHistory.Add(new ProxyConnectionHistoryItem
        {
            ConnectionId = connectionId,
            Protocol = protocol,
            RealClientIp = clientIp,
            RealClientPort = clientPort,
            LocalProxyIp = null,
            LocalProxyPort = 0,
            TargetIp = targetIp,
            TargetPort = targetPort,
            EventType = ProxyConnectionEventType.Failed,
            CreatedAtUtc = DateTime.UtcNow,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Tunnel mode after mismatch rejection: explicit <c>mode</c> if it matches node <c>PROTO</c>,
    /// otherwise the node protocol (udp-wss must not default to tcp).
    /// </summary>
    public static string ResolveProxyMode(string? modeFromQuery, string? protoFromConfig) =>
        OpenVpnProxyProtocolGuard.ResolveTunnelMode(modeFromQuery, protoFromConfig);

    private (string? Ip, int Port) GetHttpClientAddress()
    {
        var ip = ResolveClientIp(HttpContext);
        return (ip, HttpContext.Connection.RemotePort);
    }

    private static string? ResolveClientIp(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded))
        {
            var first = forwarded.ToString().Split(',').Select(s => s.Trim()).FirstOrDefault();
            if (!string.IsNullOrEmpty(first) && IPAddress.TryParse(first, out _))
                return first;
        }

        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private static async Task SafeCloseWs(WebSocket ws, WebSocketCloseStatus status, string reason)
    {
        try
        {
            await ws.CloseAsync(status, reason, CancellationToken.None);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reassembles a fragmented WS message. When <c>ownsPayload</c> is true, caller must return
    /// <paramref name="Payload"/> to <see cref="ArrayPool{T}"/> (segment is never returned by caller).
    /// </summary>
    private static async Task<(WebSocketMessageType MessageType, byte[] Payload, int PayloadLen, bool OwnsPayload)>
        ReceiveWholeWsMessageRented(WebSocket ws, byte[] segment, CancellationToken ct)
    {
        var result = await ws.ReceiveAsync(segment.AsMemory(0, segment.Length), ct);

        if (result.MessageType == WebSocketMessageType.Close)
            return (WebSocketMessageType.Close, Array.Empty<byte>(), 0, false);

        if (result.EndOfMessage)
            return (result.MessageType, segment, result.Count, false);

        // Fragmented: grow into a rented buffer.
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(WsSegmentSize * 2, result.Count + WsSegmentSize));
        var written = result.Count;
        Buffer.BlockCopy(segment, 0, rented, 0, result.Count);

        try
        {
            do
            {
                if (written + WsSegmentSize > rented.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(rented.Length * 2);
                    Buffer.BlockCopy(rented, 0, bigger, 0, written);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = bigger;
                }

                result = await ws.ReceiveAsync(rented.AsMemory(written, rented.Length - written), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    return (WebSocketMessageType.Close, Array.Empty<byte>(), 0, false);
                }

                written += result.Count;
            } while (!result.EndOfMessage);

            return (result.MessageType, rented, written, true);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private static async Task TryCloseWs(WebSocket ws, string reason, ILogger logger)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogTrace("WebSocket close failed. {Message}", ex.Message);
        }
    }

    private static async Task PumpWebSocketToTcp(
        WebSocket ws,
        NetworkStream tcp,
        CancellationToken ct,
        ILogger logger,
        IProxyFlowCounter? counter)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(WsSegmentSize);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer.AsMemory(0, buffer.Length), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Binary)
                    continue;

                await tcp.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                counter?.Add(ProxyTrafficFlowDirection.ClientToServer, result.Count);

                while (!result.EndOfMessage)
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (result.MessageType != WebSocketMessageType.Binary)
                        break;

                    await tcp.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                    counter?.Add(ProxyTrafficFlowDirection.ClientToServer, result.Count);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace("Connection cancelled. {Message}", ex.Message);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "WS->TCP pump error. {Message}", e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpTcpToWebSocket(
        WebSocket ws,
        NetworkStream tcp,
        CancellationToken ct,
        ILogger logger,
        IProxyFlowCounter? counter)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(WsSegmentSize);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var read = await tcp.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read <= 0)
                    break;

                await ws.SendAsync(
                    buffer.AsMemory(0, read),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken: ct);
                counter?.Add(ProxyTrafficFlowDirection.ServerToClient, read);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogTrace("Connection cancelled. {Message}", ex.Message);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "TCP->WS pump error. {Message}", e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}