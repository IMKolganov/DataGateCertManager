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

public class OpenVpnProxyControllerIntegrationTests
{
    [Theory]
    [InlineData(10 * 1024 * 1024)]
    public async Task TcpProxy_RoundTripsPayload_AndRecordsTraffic(int payloadBytes)
    {
        await using var echo = await TcpEchoServer.StartAsync();
        var (server, active, flow) = CreateProxyTestServer(echo.Port, proto: "tcp");

        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=tcp"), CancellationToken.None);

        var payload = CreatePayload(payloadBytes);
        await ws.SendAsync(payload, WebSocketMessageType.Binary, true, CancellationToken.None);
        var echoed = await ReceiveAtLeastBytesAsync(ws, payload.Length, CancellationToken.None);

        Assert.Equal(payload.Length, echoed.Length);
        Assert.Equal(payload, echoed);

        var observed = await WaitForTrafficAsync(flow, payload.Length, TimeSpan.FromSeconds(3));
        Assert.NotNull(observed);
        Assert.True(observed!.ClientToServerBytesTotal >= payload.Length);
        Assert.True(observed.ServerToClientBytesTotal >= payload.Length);

        await TryCloseAsync(ws);
        await WaitUntilAsync(() => active.Count == 0, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("udp", "tcp", OpenVpnProxyProtocolGuard.UdpOnlyMessage)]
    [InlineData("tcp", "udp", OpenVpnProxyProtocolGuard.TcpOnlyMessage)]
    public async Task Proxy_WhenModeDoesNotMatchNodeProto_Returns400_WithoutUpgrading(
        string nodeProto, string requestedMode, string expectedMessage)
    {
        var (server, _, _) = CreateProxyTestServer(tcpTargetPort: 1194, proto: nodeProto);
        using var client = server.CreateClient();

        using var response = await client.GetAsync($"/api/proxy?mode={requestedMode}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedMessage, body);
    }

    [Fact]
    public async Task TcpProxy_WhenConnectRefused_ClosesWebSocketWithConnectFailed()
    {
        var (server, _, _) = CreateProxyTestServer(tcpTargetPort: 1, proto: "tcp");
        using var ws = await server.CreateWebSocketClient()
            .ConnectAsync(new Uri("ws://localhost/api/proxy?mode=tcp"), CancellationToken.None);

        var buffer = new byte[16];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.InternalServerError, ws.CloseStatus);
        Assert.Equal(OpenVpnProxyProtocolGuard.TcpConnectFailedMessage, ws.CloseStatusDescription);
    }

    private static (TestServer Server, ActiveProxyConnectionService Active, ProxyTrafficFlowService Flow) CreateProxyTestServer(
        int tcpTargetPort,
        string? proto = null)
    {
        var active = new ActiveProxyConnectionService();
        var history = new ProxyConnectionHistoryService();
        var flow = new ProxyTrafficFlowService();
        var identityResolver = new ProxyConnectionIdentityResolver();

        var hostBuilder = new WebHostBuilder()
            .ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PORT"] = tcpTargetPort.ToString(),
                    ["PROTO"] = proto
                });
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton<IActiveProxyConnectionService>(active);
                services.AddSingleton<IProxyConnectionHistoryService>(history);
                services.AddSingleton<IProxyTrafficFlowService>(flow);
                services.AddSingleton<IProxyConnectionIdentityResolver>(identityResolver);
                services.AddSingleton<IProxyByteDebugService>(new NoOpProxyByteDebugService());
                services.AddSingleton<IProxyConnectionLifetimeService, ProxyConnectionLifetimeService>();
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

        return (new TestServer(hostBuilder), active, flow);
    }

    private static byte[] CreatePayload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i += 1)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static async Task<byte[]> ReceiveAtLeastBytesAsync(WebSocket ws, int expectedBytes, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (ms.Length < expectedBytes)
        {
            var res = await ws.ReceiveAsync(buffer, ct);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("WebSocket closed before payload was received.");
            if (res.MessageType != WebSocketMessageType.Binary)
                continue;

            if (res.Count > 0)
                ms.Write(buffer, 0, res.Count);
        }

        return ms.ToArray();
    }

    private static async Task TryCloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch
        {
            // remote may already close during pump teardown under load
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
            var batch = flow.BuildBatch(DateTime.UtcNow);
            var hit = batch.FirstOrDefault(x =>
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
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
    }

    private sealed class NoOpProxyByteDebugService : IProxyByteDebugService
    {
        public void ReportDisconnect(ProxyTrafficFlowUpdate update)
        {
        }
    }

    private sealed class TcpEchoServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoopTask;
        private readonly List<Task> _clientTasks = [];

        public int Port { get; }

        private TcpEchoServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoopTask = AcceptLoopAsync();
        }

        public static Task<TcpEchoServer> StartAsync()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new TcpEchoServer(listener));
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    var task = HandleClientAsync(client, _cts.Token);
                    lock (_clientTasks) _clientTasks.Add(task);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            catch (ObjectDisposedException)
            {
                // expected on shutdown
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[16 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoopTask; } catch { /* ignored */ }
            Task[] tasks;
            lock (_clientTasks) tasks = _clientTasks.ToArray();
            try { await Task.WhenAll(tasks); } catch { /* ignored */ }
            _cts.Dispose();
        }
    }
}
