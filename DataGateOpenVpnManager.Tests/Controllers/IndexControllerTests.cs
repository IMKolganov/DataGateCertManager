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
    public async Task Get_WhenConfigMissingKeys_StillReturnsOk_WithNullOrEmptyConfigValues()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var controller = new IndexController(config, _envMock.Object, _loggerMock.Object, _externalIpMock.Object);

        var result = await controller.Get(CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<RootOpenVpnInfoResponse>>(okResult.Value);
        Assert.NotNull(response.Data!.Config);
    }
}
