using System.Net.Sockets;
using DataGateOpenVpnManager.Controllers;
using DataGateOpenVpnManager.Services.Proxy;
using DataGateOpenVpnManager.Tests.Services.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Enums;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Requests;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Proxy.Responses;
using DataGateMonitor.SharedModels.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataGateOpenVpnManager.Tests.Controllers;

public class OpenVpnProxyControllerTests
{
    private static OpenVpnProxyController CreateController(
        IActiveProxyConnectionService active,
        IProxyConnectionHistoryService? history = null,
        IProxyTrafficFlowService? trafficFlow = null,
        IProxyConnectionIdentityResolver? identityResolver = null,
        IProxyByteDebugService? byteDebug = null,
        IProxyConnectionLifetimeService? lifetime = null,
        IProxySessionAuditService? sessionAudit = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var logger = new Mock<ILogger<OpenVpnProxyController>>();
        history ??= new Mock<IProxyConnectionHistoryService>().Object;
        trafficFlow ??= new Mock<IProxyTrafficFlowService>().Object;
        identityResolver ??= new Mock<IProxyConnectionIdentityResolver>().Object;
        byteDebug ??= new Mock<IProxyByteDebugService>().Object;
        lifetime ??= new Mock<IProxyConnectionLifetimeService>().Object;
        sessionAudit ??= new NoOpProxySessionAuditService();
        return new OpenVpnProxyController(
            config, logger.Object, active, history, trafficFlow, identityResolver, byteDebug, lifetime, sessionAudit,
            new ProxyBatchBufferPool());
    }

    [Theory]
    [InlineData("udp", "tcp", "udp")]
    [InlineData("TCP", "udp", "tcp")]
    [InlineData(null, "udp", "udp")]
    [InlineData("", "udp", "udp")]
    [InlineData("  ", "tcp", "tcp")]
    [InlineData(null, null, "tcp")]
    [InlineData(null, "weird", "tcp")]
    public void ResolveProxyMode_PrefersQuery_ThenProto(string? query, string? proto, string expected)
    {
        Assert.Equal(expected, OpenVpnProxyController.ResolveProxyMode(query, proto));
    }

    [Theory]
    [InlineData("tcp", "udp", OpenVpnProxyProtocolGuard.UdpOnlyMessage)]
    [InlineData("udp", "tcp", OpenVpnProxyProtocolGuard.TcpOnlyMessage)]
    [InlineData("TCP", "UDP", OpenVpnProxyProtocolGuard.UdpOnlyMessage)]
    public void TryGetMismatchMessage_WhenClientHitsWrongProto_ReturnsChannelError(
        string query, string proto, string expected)
    {
        Assert.True(OpenVpnProxyProtocolGuard.TryGetMismatchMessage(query, proto, out var message));
        Assert.Equal(expected, message);
    }

    [Theory]
    [InlineData(null, "udp")]
    [InlineData("", "tcp")]
    [InlineData("tcp", "tcp")]
    [InlineData("udp", "udp")]
    public void TryGetMismatchMessage_WhenModeMatchesOrOmitted_IsFalse(string? query, string proto)
    {
        Assert.False(OpenVpnProxyProtocolGuard.TryGetMismatchMessage(query, proto, out var message));
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void ClientMessageForConnectFailure_TcpRefusedOnUdpNode_ReturnsUdpOnly()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var message = OpenVpnProxyProtocolGuard.ClientMessageForConnectFailure("tcp", "udp", refused);
        Assert.Equal(OpenVpnProxyProtocolGuard.UdpOnlyMessage, message);
        Assert.True(OpenVpnProxyProtocolGuard.IsProtocolMismatchConnectFailure("tcp", "udp", refused));
    }

    [Fact]
    public void ClientMessageForConnectFailure_TcpRefusedOnTcpNode_StaysConnectFailed()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var message = OpenVpnProxyProtocolGuard.ClientMessageForConnectFailure("tcp", "tcp", refused);
        Assert.Equal(OpenVpnProxyProtocolGuard.TcpConnectFailedMessage, message);
        Assert.False(OpenVpnProxyProtocolGuard.IsProtocolMismatchConnectFailure("tcp", "tcp", refused));
    }

    [Fact]
    public void ClientMessageForConnectFailure_UdpRefusedOnTcpNode_ReturnsTcpOnly()
    {
        var refused = new SocketException((int)SocketError.ConnectionRefused);
        var message = OpenVpnProxyProtocolGuard.ClientMessageForConnectFailure("udp", "tcp", refused);
        Assert.Equal(OpenVpnProxyProtocolGuard.TcpOnlyMessage, message);
    }

    [Fact]
    public void GetClientByLocalPort_ReturnsNotFound_WhenNoConnection()
    {
        var active = new ActiveProxyConnectionService();
        var controller = CreateController(active);

        var result = controller.GetClientByLocalPort(new GetProxyClientByLocalPortRequest
        {
            LocalPort = 65000,
            Host = "localhost"
        });

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var notFoundBody = Assert.IsType<ApiResponse<ProxyClientLookupResponse>>(notFound.Value);
        Assert.False(notFoundBody.Success);
        Assert.Contains("No active proxy session", notFoundBody.Message);
    }

    [Fact]
    public void GetClientByLocalPort_ReturnsBadRequest_WhenPortInvalid()
    {
        var active = new ActiveProxyConnectionService();
        var controller = CreateController(active);

        var result = controller.GetClientByLocalPort(new GetProxyClientByLocalPortRequest { LocalPort = 0 });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var badRequestBody = Assert.IsType<ApiResponse<ProxyClientLookupResponse>>(badRequest.Value);
        Assert.False(badRequestBody.Success);
    }

    [Fact]
    public void GetClientByLocalPort_ReturnsOk_WithConnection()
    {
        var active = new ActiveProxyConnectionService();
        var expected = new ActiveProxyConnection
        {
            ConnectionId = "conn-1",
            Protocol = ProxyConnectionProtocol.Udp,
            RealClientIp = "192.0.2.10",
            RealClientPort = 48000,
            LocalProxyIp = "127.0.0.1",
            LocalProxyPort = 41234,
            TargetIp = "127.0.0.1",
            TargetPort = 1194,
            ConnectedAtUtc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)
        };
        active.Add(expected);

        var controller = CreateController(active);

        var result = controller.GetClientByLocalPort(new GetProxyClientByLocalPortRequest
        {
            LocalPort = 41234,
            Host = "localhost"
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var wrapped = Assert.IsType<ApiResponse<ProxyClientLookupResponse>>(ok.Value);
        Assert.True(wrapped.Success);
        Assert.NotNull(wrapped.Data);
        var value = wrapped.Data!;
        Assert.Equal("127.0.0.1", value.Host);
        Assert.Equal("conn-1", value.ConnectionId);
        Assert.Equal(ProxyConnectionProtocol.Udp, value.Protocol);
        Assert.Equal("192.0.2.10", value.RealClientIp);
        Assert.Equal(41234, value.LocalProxyPort);
    }
}
