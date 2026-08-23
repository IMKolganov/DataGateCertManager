using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Info;
using DataGateMonitor.SharedModels.Responses;
using DataGateOpenVpnManager.Controllers;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataGateOpenVpnManager.Tests.Controllers;

public class IndexControllerTests
{
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly Mock<ILogger<IndexController>> _loggerMock;
    private readonly Mock<IExternalIpAddressService> _externalIpMock;

    public IndexControllerTests()
    {
        _envMock = new Mock<IWebHostEnvironment>();
        _envMock.Setup(e => e.EnvironmentName).Returns("Testing");
        _loggerMock = new Mock<ILogger<IndexController>>();
        _externalIpMock = new Mock<IExternalIpAddressService>();
        _externalIpMock
            .Setup(x => x.GetPublicIpAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    [Fact]
    public async Task Get_ReturnsOk_WithRootOpenVpnInfoResponse()
    {
        var configData = new Dictionary<string, string?>
        {
            ["DNS1"] = "8.8.8.8",
            ["DNS2"] = "8.8.4.4",
            ["VPN_SUBNET"] = "10.51.28.0",
            ["VPN_NETMASK"] = "255.255.255.0",
            ["EASY_RSA_PATH"] = "/path/easy-rsa",
            ["DATA_DIR"] = "/data",
            ["PORT"] = "1194",
            ["API_PORT"] = "5010",
            ["PROTO"] = "udp",
            ["CIPHER"] = "AES-128-GCM",
            ["DATA_CIPHERS"] = "AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305",
            ["DCO"] = "true",
            ["AUTH"] = "SHA256",
            ["TLS_VERSION_MIN"] = "1.2",
            ["MSSFIX"] = "1200",
            ["CLIENT_VERB"] = "3",
            ["OpenVpnManagement:Host"] = "127.0.0.1",
            ["OpenVpnManagement:Port"] = "5092",
            ["BACKEND__BASEURL"] = "http://backend/"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        _externalIpMock
            .Setup(x => x.GetPublicIpAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.10");

        var controller = new IndexController(config, _envMock.Object, _loggerMock.Object, _externalIpMock.Object);

        var result = await controller.Get(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<RootOpenVpnInfoResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("DataGateOpenVpnManager", response.Data!.Application);
        Assert.Equal("Testing", response.Data.Environment);
        Assert.Equal("203.0.113.10", response.Data.PublicIp);
        Assert.NotNull(response.Data.Version);
        Assert.NotNull(response.Data.Config);
        Assert.Equal("8.8.8.8", response.Data.Config.Dns1);
        Assert.Equal("1194", response.Data.Config.Port);
        Assert.Equal("AES-128-GCM", response.Data.Config.Cipher);
        Assert.Equal("AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305", response.Data.Config.DataCiphers);
        Assert.Equal("true", response.Data.Config.Dco);
        Assert.Equal("SHA256", response.Data.Config.Auth);
        Assert.Equal("1.2", response.Data.Config.TlsVersionMin);
        Assert.Equal("1200", response.Data.Config.MssFix);
        Assert.Equal("3", response.Data.Config.ClientVerb);
        Assert.Equal("5092", response.Data.Config.OpenVpnManagement?.Port);
    }

    [Fact]
    public async Task Get_WhenPublicIpLookupFails_StillReturnsOk_WithNullPublicIp()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        _externalIpMock
            .Setup(x => x.GetPublicIpAddressAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));

        var controller = new IndexController(config, _envMock.Object, _loggerMock.Object, _externalIpMock.Object);

        var result = await controller.Get(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<RootOpenVpnInfoResponse>>(okResult.Value);
        Assert.NotNull(response.Data!.Config);
        Assert.Null(response.Data.PublicIp);
    }

    [Fact]
    public async Task Get_WhenCipherEnvEmpty_AndDcoTrue_DefaultsCipherLikeEntrypoint()
    {
        var configData = new Dictionary<string, string?>
        {
            ["DCO"] = "true",
            ["PROTO"] = "udp",
            ["PORT"] = "1194"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configData!).Build();
        var controller = new IndexController(config, _envMock.Object, _loggerMock.Object, _externalIpMock.Object);

        var result = await controller.Get(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<RootOpenVpnInfoResponse>>(okResult.Value);
        Assert.Equal("AES-128-GCM", response.Data!.Config!.Cipher);
        Assert.Equal("AES-128-GCM:AES-256-GCM:CHACHA20-POLY1305", response.Data.Config.DataCiphers);
        Assert.Equal("SHA256", response.Data.Config.Auth);
        Assert.Equal("1.2", response.Data.Config.TlsVersionMin);
    }
}
