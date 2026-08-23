using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.EasyRsaServices;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace DataGateOpenVpnManager.Tests.Services.EasyRsaServices;

public class EasyRsaServiceTests
{
    private static readonly IOptions<EasyRsaOptions> DefaultOptions = Options.Create(new EasyRsaOptions());

    [Fact]
    public async Task RevokeCertificateAsync_WhenIssuedCertMissing_ThrowsFileNotFound()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EasyRsa_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "pki", "issued"));

        var service = new EasyRsaService(
            NullLogger<IEasyRsaService>.Instance,
            Mock.Of<IEasyRsaParseDbService>(),
            Mock.Of<IBashCommandRunner>(),
            Mock.Of<IOpenVpnServerService>(),
            DefaultOptions,
            new EasyRsaPkiMutex());

        try
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                service.RevokeCertificateAsync(tempDir, "missing-client", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateEasyRsaLayout(string commonName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EasyRsa_" + Guid.NewGuid().ToString("N"));
        var issuedDir = Path.Combine(tempDir, "pki", "issued");
        Directory.CreateDirectory(issuedDir);
        File.WriteAllText(Path.Combine(issuedDir, $"{commonName}.crt"), "dummy cert");
        return tempDir;
    }
}
