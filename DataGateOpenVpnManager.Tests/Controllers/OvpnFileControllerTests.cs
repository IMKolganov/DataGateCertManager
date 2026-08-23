using DataGateOpenVpnManager.Controllers;
using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Services.Interfaces;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Requests;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Responses;
using DataGateMonitor.SharedModels.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataGateOpenVpnManager.Tests.Controllers;

public class OvpnFileControllerTests
{
    private readonly Mock<IOvpnFileService> _ovpnFileServiceMock = new();
    private readonly Mock<IEasyRsaPathResolver> _pathResolverMock = new();
    private readonly Mock<ILogger<OvpnFileController>> _loggerMock = new();

    [Fact]
    public async Task AddOvpnFile_WhenRequestValid_ReturnsOk()
    {
        var request = new GenerateOvpnFileRequest
        {
            CommonName = "client1",
            ConfigTemplate = "client\nremote {{server_ip}} {{server_port}}",
            FriendlyΝame = "Client 1",
            ServerIp = "1.2.3.4",
            ServerPort = 1194
        };
        var metadata = new OvpnFileMetadata { CommonName = "client1", FileName = "client1.ovpn", FilePath = "/path/client1.ovpn" };
        _pathResolverMock.Setup(p => p.GetEasyRsaPath()).Returns("/easy-rsa");
        _ovpnFileServiceMock.Setup(s => s.AddOvpnFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(metadata);

        var controller = new OvpnFileController(_ovpnFileServiceMock.Object, _pathResolverMock.Object, _loggerMock.Object);

        var result = await controller.AddOvpnFile(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<OvpnFileMetadata>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(metadata.CommonName, response.Data!.CommonName);
    }

    [Fact]
    public async Task AddOvpnFile_WhenCommonNameEmpty_ThrowsAndReturnsBadRequest()
    {
        var request = new GenerateOvpnFileRequest { CommonName = "", ConfigTemplate = "template" };
        _pathResolverMock.Setup(p => p.GetEasyRsaPath()).Returns("/easy-rsa");

        var controller = new OvpnFileController(_ovpnFileServiceMock.Object, _pathResolverMock.Object, _loggerMock.Object);

        var result = await controller.AddOvpnFile(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task RevokeOvpnFile_WhenRequestValid_ReturnsOk()
    {
        var request = new RevokeOvpnFileRequest { CommonName = "client1", OvpnFileName = "client1.ovpn", OvpnFilePath = "/path/client1.ovpn" };
        var metadata = new OvpnFileMetadata { CommonName = "client1", FilePath = "/revoked/client1.ovpn" };
        _pathResolverMock.Setup(p => p.GetEasyRsaPath()).Returns("/easy-rsa");
        _ovpnFileServiceMock.Setup(s => s.RevokeOvpnFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);

        var controller = new OvpnFileController(_ovpnFileServiceMock.Object, _pathResolverMock.Object, _loggerMock.Object);

        var result = await controller.RevokeOvpnFile(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<OvpnFileMetadata>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(metadata.CommonName, response.Data!.CommonName);
    }

    [Fact]
    public async Task DownloadOvpnFile_WhenFileExists_ReturnsOkWithContent()
    {
        var request = new DownloadOvpnFileRequest { FileName = "client1.ovpn", FilePath = "/path/client1.ovpn" };
        var download = new OvpnFileDownload { FileName = "client1.ovpn", Content = new byte[] { 1, 2, 3 } };
        _ovpnFileServiceMock.Setup(s => s.GetOvpnFile(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(download);

        var controller = new OvpnFileController(_ovpnFileServiceMock.Object, _pathResolverMock.Object, _loggerMock.Object);

        var result = await controller.DownloadOvpnFile(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ApiResponse<OvpnFileDownload>>(okResult.Value);
        Assert.True(response.Success);
        Assert.NotNull(response.Data!.Content);
        Assert.Equal(3, response.Data.Content.Length);
    }
}
