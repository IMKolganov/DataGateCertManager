using DataGateOpenVpnManager.Hubs;
using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.PiHole.Requests;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Services.PiHole;

public sealed class PiHoleQueryCollectorHostedService(
    IPiHoleRuntimeOptionsStore runtimeOptions,
    IPiHoleApiClient piHoleApiClient,
    IPiHoleClientIdentityResolver identityResolver,
    IPiHoleQueryCursorStore cursorStore,
    IPiHoleCollectorStatusStore statusStore,
    IOpenVpnManagementStatusCache managementStatusCache,
    IOptions<OpenVpnProxyOptions> proxyOptions,
    IHubContext<OpenVpnEventHub> eventHub,
    ILogger<PiHoleQueryCollectorHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private bool _collectorActive;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cfg = runtimeOptions.GetEffective();
            if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.BaseUrl))
            {
                if (_collectorActive)
                {
                    _collectorActive = false;
                    statusStore.SetCollectorRunning(false);
                    logger.LogInformation(
                        "Pi-hole DNS collector paused. Enabled={Enabled}, HasBaseUrl={HasBaseUrl}.",
                        cfg.Enabled,
                        !string.IsNullOrWhiteSpace(cfg.BaseUrl));
                }
                else
                {
                    logger.LogDebug(
                        "Pi-hole DNS collector idle. Enabled={Enabled}, HasBaseUrl={HasBaseUrl}.",
                        cfg.Enabled,
                        !string.IsNullOrWhiteSpace(cfg.BaseUrl));
                }

                try
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (!_collectorActive)
            {
                _collectorActive = true;
                statusStore.SetCollectorRunning(true);
                logger.LogInformation(
                    "Pi-hole DNS collector started. BaseUrl={BaseUrl}, IntervalSec={IntervalSec}, BatchSize={BatchSize}, LookbackSec={LookbackSec}, SubnetPrefix={SubnetPrefix}",
                    cfg.BaseUrl,
                    cfg.PollIntervalSeconds,
                    cfg.BatchSize,
                    cfg.LookbackSeconds,
                    cfg.ClientSubnetPrefix);
            }

            var interval = TimeSpan.FromSeconds(Math.Max(10, cfg.PollIntervalSeconds));

            try
            {
                await CollectOnceAsync(cfg, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var at = DateTimeOffset.UtcNow;
                statusStore.RecordPollFailure(at, ex.Message);
                logger.LogWarning(
                    ex,
                    "Pi-hole poll cycle failed. BaseUrl={BaseUrl}, LastError={Error}",
                    cfg.BaseUrl,
                    ex.Message);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _collectorActive = false;
        statusStore.SetCollectorRunning(false);
        logger.LogInformation("Pi-hole DNS collector stopped.");
    }

    internal async Task<int> CollectOnceAsync(PiHoleOptions cfg, CancellationToken cancellationToken)
    {
        var pollStarted = DateTimeOffset.UtcNow;
        var untilUtc = pollStarted;
        var lastUntil = cursorStore.GetLastUntilUtc();
        var fromUtc = lastUntil?.AddSeconds(-Math.Max(0, cfg.LookbackSeconds))
                      ?? untilUtc.AddSeconds(-Math.Max(cfg.LookbackSeconds, cfg.PollIntervalSeconds));

        if (fromUtc >= untilUtc)
            fromUtc = untilUtc.AddSeconds(-5);

        logger.LogDebug(
            "Pi-hole poll window: from={FromUtc:o}, until={UntilUtc:o}, batchSize={BatchSize}",
            fromUtc,
            untilUtc,
            cfg.BatchSize);

        var fetch = await piHoleApiClient.GetQueriesSinceAsync(
            fromUtc,
            untilUtc,
            cfg.BatchSize,
            cancellationToken);

        var records = fetch.Records;
        if (records.Count == 0)
        {
            cursorStore.SaveLastUntilUtc(untilUtc);
            statusStore.RecordPollSuccess(new PiHolePollSuccessResult
            {
                AtUtc = pollStarted,
                QueriesFetched = fetch.TotalFromApi,
                QueriesAfterFilter = 0,
                QueriesEnriched = 0,
                QueriesForwarded = 0,
                CursorUntilUtc = untilUtc
            });
            logger.LogDebug(
                "Pi-hole poll: no VPN-scoped queries (apiTotal={ApiTotal}). Cursor advanced to {UntilUtc:o}.",
                fetch.TotalFromApi,
                untilUtc);
            return 0;
        }

        var snapshot = await EnsureManagementSnapshotForMappingAsync(cancellationToken);

        var enriched = identityResolver.Enrich(records, snapshot);
        var mapped = enriched.Where(q => !string.IsNullOrWhiteSpace(q.CommonName)).ToList();
        if (mapped.Count == 0)
        {
            cursorStore.SaveLastUntilUtc(untilUtc);
            statusStore.RecordPollSuccess(new PiHolePollSuccessResult
            {
                AtUtc = pollStarted,
                QueriesFetched = fetch.TotalFromApi,
                QueriesAfterFilter = records.Count,
                QueriesEnriched = 0,
                QueriesForwarded = 0,
                CursorUntilUtc = untilUtc
            });
            logger.LogInformation(
                "Pi-hole poll: {AfterFilter} queries matched subnet but none mapped to VPN clients (management valid={ManagementValid}, clients={ClientCount}, cacheAgeSec={CacheAgeSec:F0}).",
                records.Count,
                snapshot?.IsValid == true,
                snapshot?.Clients.Count ?? 0,
                snapshot is null ? -1 : (DateTime.UtcNow - snapshot.FetchedAtUtc).TotalSeconds);
            return 0;
        }

        var batch = new DnsQueryBatchRequest
        {
            CollectedAtUtc = DateTimeOffset.UtcNow,
            Queries = mapped
        };

        await eventHub.Clients.All.SendAsync("DnsQueriesReceived", batch, cancellationToken);
        cursorStore.SaveLastUntilUtc(untilUtc);
        statusStore.RecordPollSuccess(new PiHolePollSuccessResult
        {
            AtUtc = pollStarted,
            QueriesFetched = fetch.TotalFromApi,
            QueriesAfterFilter = records.Count,
            QueriesEnriched = mapped.Count,
            QueriesForwarded = mapped.Count,
            CursorUntilUtc = untilUtc
        });

        logger.LogInformation(
            "Pi-hole poll OK: apiTotal={ApiTotal}, afterFilter={AfterFilter}, enriched={Enriched}, forwarded={Forwarded}, window={FromUtc:o}..{UntilUtc:o}",
            fetch.TotalFromApi,
            records.Count,
            mapped.Count,
            mapped.Count,
            fromUtc,
            untilUtc);
        return mapped.Count;
    }

    private async Task<OpenVpnManagementStatusSnapshot?> EnsureManagementSnapshotForMappingAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = managementStatusCache.GetSnapshot();
        if (!ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
                snapshot,
                proxyOptions.Value.ManagementCacheMaxAge))
            return snapshot;

        logger.LogDebug(
            "Pi-hole poll: refreshing OpenVPN management snapshot for CN mapping (clients={ClientCount}, cacheAgeSec={CacheAgeSec:F0}).",
            snapshot?.Clients.Count ?? 0,
            snapshot is null ? -1 : (DateTime.UtcNow - snapshot.FetchedAtUtc).TotalSeconds);

        await managementStatusCache.RefreshAsync(cancellationToken);
        return managementStatusCache.GetSnapshot();
    }
}
