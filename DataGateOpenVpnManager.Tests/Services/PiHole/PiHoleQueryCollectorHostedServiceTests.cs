using DataGateOpenVpnManager.Hubs;
using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.PiHole;
using DataGateOpenVpnManager.Services.Proxy;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DataGateOpenVpnManager.Tests.Services.PiHole;

public class PiHoleQueryCollectorHostedServiceTests
{
    [Fact]
    public async Task CollectOnceAsync_BroadcastsEnrichedBatch()
    {
        var api = new Mock<IPiHoleApiClient>();
        api.Setup(x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PiHoleQueryFetchResult
            {
                TotalFromApi = 1,
                Records = new[]
                {
                    new PiHoleQueryRecord(10, "10.51.30.3", "openai.com", "A", "FORWARDED", DateTimeOffset.UtcNow)
                }
            });

        var cursor = new Mock<IPiHoleQueryCursorStore>();
        cursor.Setup(x => x.GetLastUntilUtc()).Returns((DateTimeOffset?)null);

        var cache = new Mock<IOpenVpnManagementStatusCache>();
        cache.Setup(x => x.GetSnapshot()).Returns(new OpenVpnManagementStatusSnapshot(
            DateTime.UtcNow,
            "raw",
            new[]
            {
                new OpenVpnManagementClientEntry("cn-1", "1.2.3.4:1234", "10.51.30.3", 0, 0, 0)
            },
            true));

        var clientProxy = new Mock<IClientProxy>();
        clientProxy.Setup(c => c.SendCoreAsync(
                "DnsQueriesReceived",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<OpenVpnEventHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var store = PiHoleRuntimeOptionsStoreTestHelper.Create();
        store.Apply(new PiHoleOptions { BatchSize = 50, LookbackSeconds = 60, Enabled = true });

        var sut = new PiHoleQueryCollectorHostedService(
            store,
            api.Object,
            new PiHoleClientIdentityResolver(),
            cursor.Object,
            new PiHoleCollectorStatusStore(),
            cache.Object,
            Options.Create(new OpenVpnProxyOptions()),
            hub.Object,
            NullLogger<PiHoleQueryCollectorHostedService>.Instance);

        var count = await sut.CollectOnceAsync(
            new PiHoleOptions { BatchSize = 50, LookbackSeconds = 60 },
            CancellationToken.None);

        Assert.Equal(1, count);
        cursor.Verify(x => x.SaveLastUntilUtc(It.IsAny<DateTimeOffset>()), Times.Once);
        clientProxy.Verify(
            c => c.SendCoreAsync("DnsQueriesReceived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CollectOnceAsync_AdvancesCursor_WhenNoRecordsAfterFilter()
    {
        var api = new Mock<IPiHoleApiClient>();
        api.Setup(x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PiHoleQueryFetchResult { TotalFromApi = 3, Records = [] });

        var cursor = new Mock<IPiHoleQueryCursorStore>();
        cursor.Setup(x => x.GetLastUntilUtc()).Returns((DateTimeOffset?)null);

        var status = new PiHoleCollectorStatusStore();
        var sut = CreateSut(api.Object, cursor.Object, status, hubClients: null);

        var count = await sut.CollectOnceAsync(
            new PiHoleOptions { BatchSize = 50, LookbackSeconds = 60 },
            CancellationToken.None);

        Assert.Equal(0, count);
        cursor.Verify(x => x.SaveLastUntilUtc(It.IsAny<DateTimeOffset>()), Times.Once);
        var snapshot = status.GetSnapshot();
        Assert.Equal(3, snapshot.LastPollQueriesFetched);
        Assert.Equal(0, snapshot.LastPollQueriesForwarded);
        Assert.Null(snapshot.LastPollError);
    }

    [Fact]
    public async Task CollectOnceAsync_DoesNotBroadcast_WhenNoClientMapping()
    {
        var api = new Mock<IPiHoleApiClient>();
        api.Setup(x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PiHoleQueryFetchResult
            {
                TotalFromApi = 2,
                Records =
                [
                    new PiHoleQueryRecord(11, "10.51.30.99", "unknown.test", "A", "FORWARDED", DateTimeOffset.UtcNow)
                ]
            });

        var cursor = new Mock<IPiHoleQueryCursorStore>();
        cursor.Setup(x => x.GetLastUntilUtc()).Returns((DateTimeOffset?)null);

        var cache = new Mock<IOpenVpnManagementStatusCache>();
        cache.Setup(x => x.GetSnapshot()).Returns(new OpenVpnManagementStatusSnapshot(
            DateTime.UtcNow,
            "raw",
            [],
            true));
        cache.Setup(x => x.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var clientProxy = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<OpenVpnEventHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var status = new PiHoleCollectorStatusStore();
        var sut = CreateSut(api.Object, cursor.Object, status, cache: cache.Object, hubClients: hub);

        var count = await sut.CollectOnceAsync(
            new PiHoleOptions { BatchSize = 50, LookbackSeconds = 60 },
            CancellationToken.None);

        Assert.Equal(0, count);
        cache.Verify(x => x.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        clientProxy.Verify(
            c => c.SendCoreAsync("DnsQueriesReceived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var snapshot = status.GetSnapshot();
        Assert.Equal(1, snapshot.LastPollQueriesAfterFilter);
        Assert.Equal(0, snapshot.LastPollQueriesEnriched);
    }

    [Fact]
    public async Task CollectOnceAsync_RefreshesEmptyManagementSnapshot_ThenMaps()
    {
        var api = new Mock<IPiHoleApiClient>();
        api.Setup(x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PiHoleQueryFetchResult
            {
                TotalFromApi = 1,
                Records =
                [
                    new PiHoleQueryRecord(12, "10.51.30.3", "mapped.test", "A", "FORWARDED", DateTimeOffset.UtcNow)
                ]
            });

        var cursor = new Mock<IPiHoleQueryCursorStore>();
        cursor.Setup(x => x.GetLastUntilUtc()).Returns((DateTimeOffset?)null);

        var empty = new OpenVpnManagementStatusSnapshot(DateTime.UtcNow.AddDays(-4), "TITLE\tEND", [], true);
        var filled = new OpenVpnManagementStatusSnapshot(
            DateTime.UtcNow,
            "raw",
            [new OpenVpnManagementClientEntry("cn-1", "1.2.3.4:1234", "10.51.30.3", 0, 0, 0)],
            true);

        var cache = new Mock<IOpenVpnManagementStatusCache>();
        cache.SetupSequence(x => x.GetSnapshot())
            .Returns(empty)
            .Returns(filled);
        cache.Setup(x => x.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy.Setup(c => c.SendCoreAsync(
                "DnsQueriesReceived",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<OpenVpnEventHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var status = new PiHoleCollectorStatusStore();
        var sut = CreateSut(api.Object, cursor.Object, status, cache: cache.Object, hubClients: hub);

        var count = await sut.CollectOnceAsync(
            new PiHoleOptions { BatchSize = 50, LookbackSeconds = 60 },
            CancellationToken.None);

        Assert.Equal(1, count);
        cache.Verify(x => x.RefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        clientProxy.Verify(
            c => c.SendCoreAsync("DnsQueriesReceived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_StartsCollectingAfterRuntimeEnable()
    {
        var store = PiHoleRuntimeOptionsStoreTestHelper.Create();
        var api = new Mock<IPiHoleApiClient>();
        api.Setup(x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PiHoleQueryFetchResult { TotalFromApi = 0, Records = [] });

        var status = new PiHoleCollectorStatusStore();
        var sut = CreateSut(api.Object, new Mock<IPiHoleQueryCursorStore>().Object, status, store: store);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _ = sut.StartAsync(cts.Token);
        await Task.Delay(150, CancellationToken.None);
        store.Apply(new PiHoleOptions
        {
            Enabled = true,
            BaseUrl = "http://127.0.0.1:8080",
            AppPassword = "secret",
            PollIntervalSeconds = 10,
            BatchSize = 50,
            LookbackSeconds = 60,
        });

        // First idle cycle is 5s; config must be applied before that wake-up.
        await Task.Delay(5100, CancellationToken.None);

        api.Verify(
            x => x.GetQueriesSinceAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        Assert.True(status.GetSnapshot().CollectorRunning || status.GetSnapshot().LastPollAtUtc is not null);
    }

    private static PiHoleQueryCollectorHostedService CreateSut(
        IPiHoleApiClient api,
        IPiHoleQueryCursorStore cursor,
        IPiHoleCollectorStatusStore status,
        IPiHoleRuntimeOptionsStore? store = null,
        IOpenVpnManagementStatusCache? cache = null,
        Mock<IHubContext<OpenVpnEventHub>>? hubClients = null)
    {
        var managementCache = cache ?? CreateDefaultCache();
        var hub = hubClients ?? CreateDefaultHub();

        return new PiHoleQueryCollectorHostedService(
            store ?? PiHoleRuntimeOptionsStoreTestHelper.Create(new PiHoleOptions { Enabled = true }),
            api,
            new PiHoleClientIdentityResolver(),
            cursor,
            status,
            managementCache,
            Options.Create(new OpenVpnProxyOptions()),
            hub.Object,
            NullLogger<PiHoleQueryCollectorHostedService>.Instance);
    }

    private static IOpenVpnManagementStatusCache CreateDefaultCache()
    {
        var cache = new Mock<IOpenVpnManagementStatusCache>();
        cache.Setup(x => x.GetSnapshot()).Returns(new OpenVpnManagementStatusSnapshot(
            DateTime.UtcNow,
            "raw",
            [new OpenVpnManagementClientEntry("cn-1", "1.2.3.4:1234", "10.51.30.3", 0, 0, 0)],
            true));
        return cache.Object;
    }

    private static Mock<IHubContext<OpenVpnEventHub>> CreateDefaultHub()
    {
        var clientProxy = new Mock<IClientProxy>();
        clientProxy.Setup(c => c.SendCoreAsync(
                "DnsQueriesReceived",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<OpenVpnEventHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub;
    }

    private sealed class TestOptionsMonitor(PiHoleOptions current) : IOptionsMonitor<PiHoleOptions>
    {
        public PiHoleOptions CurrentValue { get; } = current;
        public PiHoleOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<PiHoleOptions, string?> listener) => null;
    }
}
