using System.Net;
using System.Net.Sockets;
using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.PiHole;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Diagnostics.Responses;
using DataGateMonitor.SharedModels.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController(
    IConfiguration config,
    IOptions<OpenVpnProxyOptions> proxyOptions,
    IActiveProxyConnectionService activeProxyConnections,
    IProxyTrafficFlowService trafficFlow,
    IOpenVpnManagementStatusCache statusCache,
    IProxySessionAuditService sessionAudit,
    IPiHoleRuntimeOptionsStore runtimeOptions,
    IPiHoleApiClient piHoleApiClient,
    IPiHoleCollectorStatusStore statusStore,
    IPiHoleQueryCursorStore cursorStore,
    ILogger<DiagnosticsController> logger) : ControllerBase
{
    [HttpGet("proxy-audit")]
    public ActionResult<ApiResponse<ProxyAuditDiagnosticsResponse>> GetProxyAudit([FromQuery] int limit = 100)
    {
        var entries = sessionAudit.GetRecent(limit)
            .Select(MapAuditEntry)
            .ToList();
        return Ok(ApiResponse<ProxyAuditDiagnosticsResponse>.SuccessResponse(new ProxyAuditDiagnosticsResponse
        {
            CheckedAtUtc = DateTime.UtcNow,
            Count = entries.Count,
            Entries = entries
        }));
    }

    [HttpGet("proxy-sessions")]
    public async Task<ActionResult<ApiResponse<ProxySessionDiagnosticsResponse>>> GetProxySessions(
        CancellationToken cancellationToken)
    {
        try
        {
            var dnsTarget = config["DNS1"] ?? "10.51.15.1";
            var snapshot = statusCache.GetSnapshot();
            if (ProxyManagementPeerDiagnostics.NeedsRefreshForClientMapping(
                    snapshot,
                    proxyOptions.Value.ManagementCacheMaxAge))
            {
                await statusCache.RefreshAsync(cancellationToken);
                snapshot = statusCache.GetSnapshot();
            }

            var mgmtClients = snapshot?.Clients.ToList() ?? [];
            var dns = await ProbeDnsAsync(dnsTarget, cancellationToken);
            var canEvaluatePeers = ProxyManagementPeerDiagnostics.CanEvaluatePeerPresence(
                snapshot,
                proxyOptions.Value,
                out var peerEvaluationSkipReason);

            var sessions = activeProxyConnections.GetAll()
                .Select(conn =>
                {
                    trafficFlow.TryGetTotals(conn.ConnectionId, out var c2s, out var s2c);
                    var mgmt = OpenVpnManagementStatusParser.FindByLocalProxyPort(
                        mgmtClients,
                        conn.LocalProxyIp,
                        conn.LocalProxyPort);
                    var missingFromManagement = mgmt is null;
                    var isZombie = canEvaluatePeers
                                   && snapshot is not null
                                   && ProxyManagementPeerDiagnostics.IsLikelyZombie(conn, mgmt, snapshot);

                    return new ProxySessionDiagnosticItem
                    {
                        ConnectionId = conn.ConnectionId,
                        Protocol = conn.Protocol.ToString(),
                        RealClient = $"{conn.RealClientIp}:{conn.RealClientPort}",
                        LocalProxy = $"{conn.LocalProxyIp}:{conn.LocalProxyPort}",
                        ConnectedAtUtc = conn.ConnectedAtUtc,
                        ProxyClientToServerBytes = c2s,
                        ProxyServerToClientBytes = s2c,
                        InOpenVpnManagement = mgmt is not null,
                        MissingFromManagement = missingFromManagement,
                        IsZombie = isZombie,
                        OpenVpnCommonName = mgmt?.CommonName,
                        OpenVpnVirtualAddress = mgmt?.VirtualAddress,
                        ManagementBytesReceived = mgmt?.BytesReceived,
                        ManagementBytesSent = mgmt?.BytesSent
                    };
                })
                .OrderBy(s => s.ConnectedAtUtc)
                .ToList();

            var response = new ProxySessionDiagnosticsResponse
            {
                CheckedAtUtc = DateTime.UtcNow,
                ManagementStatusAvailable = snapshot?.IsValid == true,
                ManagementStatusAgeSeconds = snapshot is null
                    ? null
                    : (DateTime.UtcNow - snapshot.FetchedAtUtc).TotalSeconds,
                ManagementClientCount = mgmtClients.Count,
                PeerEvaluationAvailable = canEvaluatePeers,
                PeerEvaluationSkipReason = peerEvaluationSkipReason,
                ActiveProxySessionCount = sessions.Count,
                ZombieSessionCount = sessions.Count(s => s.IsZombie),
                DnsProbeTarget = dnsTarget,
                DnsProbeScope = "host-default-route",
                DnsProbeNote =
                    "UDP probe uses the host default route, not the VPN client path (e.g. tun1/UFW may differ).",
                DnsProbe = dns,
                Sessions = sessions
            };

            return Ok(ApiResponse<ProxySessionDiagnosticsResponse>.SuccessResponse(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proxy session diagnostics failed");
            return BadRequest(ApiResponse<ProxySessionDiagnosticsResponse>.ErrorResponse(ex.Message));
        }
    }

    [HttpGet("pi-hole")]
    public async Task<ActionResult<ApiResponse<PiHoleDiagnosticsResponse>>> GetPiHoleDiagnostics(
        CancellationToken cancellationToken)
    {
        try
        {
            var options = runtimeOptions.GetEffective();
            var probe = await piHoleApiClient.ProbeAsync(cancellationToken);
            var response = PiHoleDiagnosticsFactory.Create(
                options,
                statusStore.GetSnapshot(),
                cursorStore.GetLastUntilUtc(),
                probe,
                runtimeOptions.PersistedAppliedAtUtc);
            return Ok(ApiResponse<PiHoleDiagnosticsResponse>.SuccessResponse(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pi-hole diagnostics failed.");
            return BadRequest(ApiResponse<PiHoleDiagnosticsResponse>.ErrorResponse(ex.Message));
        }
    }

    private static ProxySessionAuditEntryDto MapAuditEntry(ProxySessionAuditEntry entry) => new()
    {
        AtUtc = entry.AtUtc,
        Event = entry.Event,
        ConnectionId = entry.ConnectionId,
        Decision = entry.Decision,
        Reason = entry.Reason,
        Details = entry.Details
    };

    private static async Task<DnsProbeResult> ProbeDnsAsync(string dnsHost, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(dnsHost, out var ip))
        {
            return new DnsProbeResult
            {
                Host = dnsHost,
                Port = 53,
                Error = "DNS host is not an IPv4 address"
            };
        }

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2000;
            udp.Client.SendTimeout = 2000;
            var query = BuildDnsQuery("youtube.com");
            await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 53));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            var reply = await udp.ReceiveAsync(timeoutCts.Token);
            return new DnsProbeResult
            {
                Host = dnsHost,
                Port = 53,
                Responded = reply.Buffer.Length > 0,
                ResponseBytes = reply.Buffer.Length
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsProbeResult
            {
                Host = dnsHost,
                Port = 53,
                Error = "UDP DNS query timed out after 2s"
            };
        }
        catch (Exception ex)
        {
            return new DnsProbeResult
            {
                Host = dnsHost,
                Port = 53,
                Error = ex.Message
            };
        }
    }

    private static byte[] BuildDnsQuery(string hostName)
    {
        // Minimal standard query: ID=0xAA55, recursion desired, one A question.
        var hostLabels = hostName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var ms = new MemoryStream();
        ms.WriteByte(0xAA);
        ms.WriteByte(0x55);
        ms.WriteByte(0x01); // flags hi
        ms.WriteByte(0x00); // flags lo
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QDCOUNT
        ms.WriteByte(0x00); ms.WriteByte(0x00); // ANCOUNT
        ms.WriteByte(0x00); ms.WriteByte(0x00); // NSCOUNT
        ms.WriteByte(0x00); ms.WriteByte(0x00); // ARCOUNT
        foreach (var label in hostLabels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x01); // TYPE A
        ms.WriteByte(0x00); ms.WriteByte(0x01); // CLASS IN
        return ms.ToArray();
    }
}
