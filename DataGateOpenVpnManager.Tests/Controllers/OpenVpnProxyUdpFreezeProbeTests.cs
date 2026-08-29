using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using DataGateOpenVpnManager.Controllers;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateOpenVpnManager.Tests.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataGateOpenVpnManager.Tests.Controllers;

/// <summary>
/// Probes whether UDP WSS proxy sessions stick after client teardown on the current (pre-fix) code.
/// Failures with timeout / leftover active sessions would confirm a server-side freeze;
/// clean pass suggests the hang is elsewhere (client network, idle path, etc.).
/// </summary>
public class OpenVpnProxyUdpFreezeProbeTests
{
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 15_000)]
    public async Task UdpProxy_RoundTripsFramedPayload()
    {
        await using var echo = await UdpEchoServer.StartAsync();
        var (server, active, flow) = CreateProxyTestServer(echo.Port);

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=udp"), CancellationToken.None);

        var payload = CreatePayload(1400);
        await ws.SendAsync(FrameUdp(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
        var echoed = await ReceiveFramedAsync(ws, payload.Length, CancellationToken.None);

        Assert.Equal(payload, echoed);
        Assert.NotNull(await WaitForTrafficAsync(flow, payload.Length, TimeSpan.FromSeconds(3)));

        await TryCloseAsync(ws);
        await WaitUntilAsync(() => active.Count == 0, CleanupBudget);
    }

    [Fact(Timeout = 15_000)]
    public async Task UdpProxy_GracefulClientClose_UnregistersWithinBudget()
    {
        await using var echo = await UdpEchoServer.StartAsync();
        var (server, active, _) = CreateProxyTestServer(echo.Port);

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=udp"), CancellationToken.None);

        await WaitUntilAsync(() => active.Count == 1, TimeSpan.FromSeconds(3));
        await TryCloseAsync(ws);
        await WaitUntilAsync(() => active.Count == 0, CleanupBudget);
    }

    [Fact(Timeout = 15_000)]
    public async Task UdpProxy_AbruptAbort_UnregistersWithinBudget()
    {
        await using var echo = await UdpEchoServer.StartAsync();
        var (server, active, _) = CreateProxyTestServer(echo.Port);

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=udp"), CancellationToken.None);

        await WaitUntilAsync(() => active.Count == 1, TimeSpan.FromSeconds(3));
        ws.Abort();
        await WaitUntilAsync(() => active.Count == 0, CleanupBudget);
    }

    [Fact(Timeout = 15_000)]
    public async Task UdpProxy_LifetimeTerminate_UnregistersWithinBudget()
    {
        await using var echo = await UdpEchoServer.StartAsync();
        var (server, active, _, lifetime) = CreateProxyTestServerFull(echo.Port);

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=udp"), CancellationToken.None);

        await WaitUntilAsync(() => active.Count == 1, TimeSpan.FromSeconds(3));
        var conn = active.GetAll().Single();
        Assert.True(lifetime.TryTerminate(conn.ConnectionId, "freeze-probe"));
        await WaitUntilAsync(() => active.Count == 0, CleanupBudget);

        await TryCloseAsync(ws);
    }

    [Fact(Timeout = 20_000)]
    public async Task UdpProxy_AfterTraffic_ClientClose_UnregistersWithinBudget()
    {
        await using var echo = await UdpEchoServer.StartAsync();
        var (server, active, _) = CreateProxyTestServer(echo.Port);

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=udp"), CancellationToken.None);

        var payload = CreatePayload(512);
        await ws.SendAsync(FrameUdp(payload), WebSocketMessageType.Binary, true, CancellationToken.None);
        _ = await ReceiveFramedAsync(ws, payload.Length, CancellationToken.None);

        await TryCloseAsync(ws);
        await WaitUntilAsync(() => active.Count == 0, CleanupBudget);
    }

    private static (TestServer Server, ActiveProxyConnectionService Active, ProxyTrafficFlowService Flow)
        CreateProxyTestServer(int udpPort)
    {
        var full = CreateProxyTestServerFull(udpPort);
        return (full.Server, full.Active, full.Flow);
    }

    private static (
        TestServer Server,
        ActiveProxyConnectionService Active,
        ProxyTrafficFlowService Flow,
        IProxyConnectionLifetimeService Lifetime) CreateProxyTestServerFull(int udpPort)
    {
        var active = new ActiveProxyConnectionService();
        var history = new ProxyConnectionHistoryService();
        var flow = new ProxyTrafficFlowService();
        var identityResolver = new ProxyConnectionIdentityResolver();
        var lifetime = new ProxyConnectionLifetimeService(
            new NoOpProxySessionAuditService(),
            NullLogger<ProxyConnectionLifetimeService>.Instance);

        var hostBuilder = new WebHostBuilder()
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PORT"] = udpPort.ToString(),
                    ["PROTO"] = "udp"
                });
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IActiveProxyConnectionService>(active);
                services.AddSingleton<IProxyConnectionHistoryService>(history);
                services.AddSingleton<IProxyTrafficFlowService>(flow);
                services.AddSingleton<IProxyConnectionIdentityResolver>(identityResolver);
                services.AddSingleton<IProxyByteDebugService>(new NoOpProxyByteDebugService());
                services.AddSingleton<IProxyConnectionLifetimeService>(lifetime);
                services.AddSingleton<IProxySessionAuditService, NoOpProxySessionAuditService>();
                services.AddSingleton<ProxyBatchBufferPool>();
                services.AddSingleton(NullLogger<OpenVpnProxyController>.Instance);
                services.AddControllers().AddApplicationPart(typeof(OpenVpnProxyController).Assembly);
            })
            .Configure(app =>
            {
                app.UseWebSockets();
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapControllers());
            });

        return (new TestServer(hostBuilder), active, flow, lifetime);
    }

    private static byte[] FrameUdp(byte[] payload)
    {
        var framed = new byte[2 + payload.Length];
        UdpWsFraming.WriteFrame(framed, payload);
        return framed;
    }

    private static byte[] CreatePayload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i += 1)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static async Task<byte[]> ReceiveFramedAsync(WebSocket ws, int expectedPayloadBytes, CancellationToken ct)
    {
        var buffer = new byte[UdpWsFraming.BatchCapacityBytes];
        using var ms = new MemoryStream();
        while (ms.Length < 2 + expectedPayloadBytes)
        {
            var res = await ws.ReceiveAsync(buffer, ct);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before framed UDP payload was received.");
            if (res.MessageType != WebSocketMessageType.Binary || res.Count == 0)
                continue;
            ms.Write(buffer, 0, res.Count);
        }

        var data = ms.ToArray();
        var next = UdpWsFraming.TryParseNextFrame(data, 0, out var frame);
        Assert.True(next > 0);
        return frame.ToArray();
    }

    private static async Task TryCloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // remote may already be gone
        }
    }

    private static async Task<ProxyTrafficFlowUpdate?> WaitForTrafficAsync(
        ProxyTrafficFlowService flow,
        int atLeastBytes,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            var hit = flow.BuildBatch(DateTime.UtcNow).FirstOrDefault(x =>
                x.ClientToServerBytesTotal >= atLeastBytes &&
                x.ServerToClientBytesTotal >= atLeastBytes);
            if (hit is not null)
                return hit;
            await Task.Delay(25);
        }

        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Condition was not met before {timeout.TotalSeconds:0.#}s — possible UDP proxy freeze.");
    }

    private sealed class NoOpProxyByteDebugService : IProxyByteDebugService
    {
        public void ReportDisconnect(ProxyTrafficFlowUpdate update)
        {
        }
    }

    private sealed class UdpEchoServer : IAsyncDisposable
    {
        private readonly UdpClient _client;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _receiveLoopTask;

        public int Port { get; }

        private UdpEchoServer(UdpClient client)
        {
            _client = client;
            Port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
            _receiveLoopTask = ReceiveLoopAsync();
        }

        public static Task<UdpEchoServer> StartAsync()
        {
            var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return Task.FromResult(new UdpEchoServer(client));
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var result = await _client.ReceiveAsync(_cts.Token);
                    await _client.SendAsync(result.Buffer, result.RemoteEndPoint, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _client.Dispose();
            try { await _receiveLoopTask; } catch { /* ignored */ }
            _cts.Dispose();
        }
    }
}
