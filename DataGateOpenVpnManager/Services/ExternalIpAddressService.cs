using System.Net;
using System.Net.Sockets;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;

namespace DataGateOpenVpnManager.Services;

public sealed class ExternalIpAddressService(
    ILogger<ExternalIpAddressService> logger,
    IConfiguration configuration,
    HttpClient httpClient,
    IMemoryCache memoryCache) : IExternalIpAddressService
{
    private const string CacheKey = "node-public-ip";
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim FetchLock = new(1, 1);

    private readonly List<string>? _externalIpServices = configuration
        .GetSection("ExternalIpServices")
        .Get<List<string>>();

    public async Task<string?> GetPublicIpAddressAsync(CancellationToken cancellationToken)
    {
        if (TryGetCached(out var cached, out var hit))
            return hit ? cached : null;

        await FetchLock.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(out cached, out hit))
                return hit ? cached : null;

            return await FetchAndCacheAsync(cancellationToken);
        }
        finally
        {
            FetchLock.Release();
        }
    }

    /// <summary>
    /// Returns true when a cache entry exists (including negative/empty miss).
    /// </summary>
    private bool TryGetCached(out string? ip, out bool positiveHit)
    {
        if (!memoryCache.TryGetValue(CacheKey, out string? cached))
        {
            ip = null;
            positiveHit = false;
            return false;
        }

        if (string.IsNullOrWhiteSpace(cached))
        {
            ip = null;
            positiveHit = false;
            return true;
        }

        ip = cached;
        positiveHit = true;
        return true;
    }

    private async Task<string?> FetchAndCacheAsync(CancellationToken cancellationToken)
    {
        if (_externalIpServices is not { Count: > 0 })
        {
            logger.LogWarning("No ExternalIpServices configured; PublicIp will be null.");
            memoryCache.Set(CacheKey, string.Empty, NegativeTtl);
            return null;
        }

        foreach (var service in _externalIpServices)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                using var request = new HttpRequestMessage(HttpMethod.Get, service);
                request.Headers.Accept.ParseAdd("text/plain");
                using var response = await httpClient.SendAsync(request, timeoutCts.Token);
                response.EnsureSuccessStatusCode();
                var raw = (await response.Content.ReadAsStringAsync(timeoutCts.Token)).Trim();
                if (!TryParsePublicIp(raw, out var ip))
                {
                    logger.LogWarning("Ignoring non-IP response from {Service}", service);
                    continue;
                }

                memoryCache.Set(CacheKey, ip, PositiveTtl);
                logger.LogInformation("Retrieved public IP {Ip} from {Service}", ip, service);
                return ip;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Failed to get public IP from {Service}", service);
            }
        }

        logger.LogWarning("Unable to retrieve public IP from any configured service.");
        memoryCache.Set(CacheKey, string.Empty, NegativeTtl);
        return null;
    }

    internal static bool TryParsePublicIp(string? raw, out string ip)
    {
        ip = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // Take first token/line in case providers append whitespace or noise.
        var candidate = raw.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        if (!IPAddress.TryParse(candidate, out var address))
            return false;

        // Dashboard / OpenVPN remote expect the public IPv4 clients connect to.
        // Dual-stack providers (e.g. api64.ipify.org) often return IPv6 first — skip those.
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
            return false;

        ip = address.ToString();
        return true;
    }
}
