using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Cert.Responses;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Responses;

namespace DataGateOpenVpnManager.Tests.Services;

public class OvpnFileServiceTests
{
    private readonly Mock<ILogger<IOvpnFileService>> _loggerMock = new();
    private readonly Mock<IEasyRsaService> _easyRsaMock = new();
    private readonly Mock<IOvpnIssuanceTracker> _issuanceTrackerMock = new();
    private readonly IOptions<EasyRsaOptions> _options = Options.Create(new EasyRsaOptions());

    public OvpnFileServiceTests()
    {
        var lease = new Mock<IOvpnIssuanceLease>();
        lease.Setup(l => l.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _issuanceTrackerMock.Setup(t => t.Begin(It.IsAny<string>())).Returns(lease.Object);
    }

    private OvpnFileService CreateService(IConfiguration? configuration = null) =>
        new(_loggerMock.Object, _easyRsaMock.Object, _options, _issuanceTrackerMock.Object,
            configuration ?? new ConfigurationBuilder().AddInMemoryCollection().Build());

    [Fact]
    public async Task RevokeOvpnFile_WhenFileExists_MovesFileAndReturnsMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OvpnRevoke_" + Guid.NewGuid().ToString("N"));
        var ovpnPath = Path.Combine(tempDir, "client1.ovpn");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(ovpnPath, "client");

        _easyRsaMock.Setup(s => s.RevokeCertificateAsync(It.IsAny<string>(), "client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerCertificate { Message = "Revoked", CertificatePath = "/pki/revoked/client1.crt", KeyPath = "/pki/private/client1.key" });

        var service = CreateService();

        try
        {
            var result = await service.RevokeOvpnFile(tempDir, "client1", "client1.ovpn", ovpnPath, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("client1", result.CommonName);
            Assert.Equal("client1.ovpn", result.FileName);
            Assert.Contains("revoked", result.FilePath);
            Assert.False(File.Exists(ovpnPath));
            var revokedDir = Path.Combine(tempDir, "pki", "revoked", "ovpn_files");
            Assert.True(Directory.Exists(revokedDir));
            Assert.Single(Directory.GetFiles(revokedDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RevokeOvpnFile_WhenFileMissing_StillReturnsMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OvpnRevoke_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var missingPath = Path.Combine(tempDir, "missing.ovpn");

        _easyRsaMock.Setup(s => s.RevokeCertificateAsync(It.IsAny<string>(), "client1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerCertificate { Message = "Revoked" });

        var service = CreateService();

        try
        {
            var result = await service.RevokeOvpnFile(tempDir, "client1", "missing.ovpn", missingPath, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("client1", result.CommonName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetOvpnFile_WhenFileExists_ReturnsContent()
    {
        var tempFile = Path.GetTempFileName();
        var expectedContent = "client\nremote 1.2.3.4 1194";
        await File.WriteAllTextAsync(tempFile, expectedContent);

        try
        {
            var service = CreateService();
            var result = await service.GetOvpnFile("test.ovpn", tempFile, commonName: null, CancellationToken.None);

            Assert.Equal("test.ovpn", result.FileName);
            Assert.NotNull(result.Content);
            var text = System.Text.Encoding.UTF8.GetString(result.Content);
            Assert.Equal(expectedContent, text);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task GetOvpnFile_WhenFileDoesNotExist_AndNoCommonName_Throws()
    {
        var service = CreateService();
        var path = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N") + ".ovpn");

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.GetOvpnFile("nope.ovpn", path, commonName: null, CancellationToken.None));

        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task GetOvpnFile_WhenFileMissing_WaitsOnIssuanceTracker()
    {
        var path = Path.Combine(Path.GetTempPath(), "wait_" + Guid.NewGuid().ToString("N") + ".ovpn");
        await File.WriteAllTextAsync(path, "ovpn-body");

        _issuanceTrackerMock
            .Setup(t => t.WaitUntilReadyAsync("client1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        try
        {
            // Delete so Get hits the wait path, then tracker "ready" and we re-read.
            File.Delete(path);
            _issuanceTrackerMock
                .Setup(t => t.WaitUntilReadyAsync("client1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback(() => File.WriteAllText(path, "ovpn-body"))
                .Returns(Task.CompletedTask);

            var service = CreateService();
            var result = await service.GetOvpnFile("client1.ovpn", path, "client1", CancellationToken.None);

            Assert.Equal("client1", result.CommonName);
            Assert.Equal("ovpn-body", System.Text.Encoding.UTF8.GetString(result.Content!));
            _issuanceTrackerMock.Verify(
                t => t.WaitUntilReadyAsync("client1", It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
