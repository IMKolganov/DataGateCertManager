using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;
using DataGateOpenVpnManager.Services.Proxy;

namespace DataGateOpenVpnManager.Tests.Services.Proxy;

public class ProxyTrafficFlowServiceCounterTests
{
    [Fact]
    public void GetCounter_Add_IsExact_UnderParallelAdds()
    {
        var flow = new ProxyTrafficFlowService();
        flow.RegisterConnection(new ActiveProxyConnection
        {
            ConnectionId = "c1",
            Protocol = ProxyConnectionProtocol.Udp,
            LocalProxyIp = "127.0.0.1",
            LocalProxyPort = 1,
            TargetIp = "127.0.0.1",
            TargetPort = 1194,
            ConnectedAtUtc = DateTime.UtcNow
        });

        var counter = flow.GetCounter("c1");
        Assert.NotNull(counter);

        const int threads = 8;
        const int perThread = 100_000;
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < perThread; i++)
            {
                counter!.Add(ProxyTrafficFlowDirection.ClientToServer, 3);
                counter.Add(ProxyTrafficFlowDirection.ServerToClient, 5);
            }
        });

        Assert.True(flow.TryGetTotals("c1", out var c2s, out var s2c));
        Assert.Equal(threads * perThread * 3L, c2s);
        Assert.Equal(threads * perThread * 5L, s2c);

        var batch = flow.BuildBatch(DateTime.UtcNow).Single(x => x.ConnectionId == "c1");
        Assert.Equal(threads * perThread * 3L, batch.ClientToServerBytesTotal);
        Assert.Equal(threads * perThread * 5L, batch.ServerToClientBytesTotal);
        Assert.Equal(threads * perThread * 3L, batch.ClientToServerBytesDelta);
        Assert.Equal(threads * perThread * 5L, batch.ServerToClientBytesDelta);

        var batch2 = flow.BuildBatch(DateTime.UtcNow).Single(x => x.ConnectionId == "c1");
        Assert.Equal(0, batch2.ClientToServerBytesDelta);
        Assert.Equal(0, batch2.ServerToClientBytesDelta);
    }

    [Fact]
    public void ProxyBatchBufferPool_BalancesRentReturn()
    {
        var pool = new ProxyBatchBufferPool();
        var buffers = Enumerable.Range(0, 32).Select(_ => pool.Rent()).ToArray();
        foreach (var b in buffers)
        {
            Assert.Equal(ProxyBatchBufferPool.BufferSize, b.Length);
            pool.Return(b);
        }

        Assert.Equal(pool.Rented, pool.Returned);
    }
}
