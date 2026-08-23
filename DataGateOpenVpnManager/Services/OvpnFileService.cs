using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Models;
using DataGateOpenVpnManager.Services.EasyRsaServices.Interfaces;
using DataGateOpenVpnManager.Services.Interfaces;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Responses;
using Microsoft.Extensions.Options;

namespace DataGateOpenVpnManager.Services;

public class OvpnFileService(
    ILogger<IOvpnFileService> logger,
    IEasyRsaService easyRsaService,
    IOptions<EasyRsaOptions> options,
    IOvpnIssuanceTracker issuanceTracker,
    IConfiguration configuration)
    : IOvpnFileService
{
    public async Task<OvpnFileMetadata> AddOvpnFile(string easyRsaPath, string commonName, string friendlyΝame,
        string configTemplate, 
        string serverIp, int serverPort, CancellationToken cancellationToken, 
        string issuedTo = "openVpnClient", int certExpireDays = 365)
    {
        easyRsaPath = Path.GetFullPath(easyRsaPath);
        await using var issuance = issuanceTracker.Begin(commonName);
        try
        {
            var ovpnFileDir = Path.Combine(easyRsaPath, "pki", "ovpn_files");
            logger.LogInformation("Step 1: Building client certificate...");
            var certResult = await easyRsaService.BuildCertificateAsync(easyRsaPath, 
                cancellationToken, commonName, certExpireDays);

            var caCertPath = Path.Combine(easyRsaPath, "pki", "ca.crt");
            var caCertContent = await ReadPemContentAsync(
                    caCertPath ?? throw new InvalidOperationException("CaCertPath is null."),
                    cancellationToken);
            var clientCertContent = await ReadPemContentAsync(
                certResult.CertificatePath ?? throw new InvalidOperationException("CertificatePath is null."), 
                cancellationToken);
            var clientKeyContent =
                await File.ReadAllTextAsync(certResult.KeyPath ?? throw new InvalidOperationException("KeyPath is null."),
                    cancellationToken);
            var taKeyPath = Path.Combine(easyRsaPath, "pki", options.Value.TaKeyFileName);
            var taKeyContent =
                await  File.ReadAllTextAsync(taKeyPath ?? throw new InvalidOperationException("TaCertPath is null."),
                    cancellationToken);
            
            logger.LogInformation("Step 3: Generating .ovpn file...");//todo:null or empty configTemplate
            var ovpnContent = GenerateOvpnFile(configTemplate, friendlyΝame,serverIp, serverPort, caCertContent, 
                clientCertContent, clientKeyContent, taKeyContent);

            logger.LogInformation("Step 4: Writing .ovpn file...");

            var targetDir = ovpnFileDir ?? throw new InvalidOperationException("OvpnFileDir is null.");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var ovpnFilePath = Path.Combine(targetDir, $"{commonName}.ovpn");
            await File.WriteAllTextAsync(ovpnFilePath, ovpnContent, cancellationToken);

            logger.LogInformation("Client configuration file created: {Path}", ovpnFilePath);

            var fileInfo = new FileInfo(Path.GetFullPath(ovpnFilePath));
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("OVPN file was not created as expected.", fileInfo.FullName);
            }

            var issuedOvpnFile = new OvpnFileMetadata
            {
                CommonName = commonName,
                FileName = fileInfo.Name,
                FilePath = fileInfo.FullName,
                IssuedAt = DateTime.UtcNow,
                IssuedTo = issuedTo,
                CertFilePath = certResult.CertificatePath,
                KeyFilePath = certResult.KeyPath,
            };

            issuance.MarkCompleted();
            return issuedOvpnFile;
        }
        catch (Exception ex)
        {
            issuance.MarkFailed(ex);
            throw;
        }
    }

    public async Task<OvpnFileMetadata?> RevokeOvpnFile(string easyRsaPath, string commonName, 
        string ovpnFileName, string ovpnFilePath, CancellationToken cancellationToken)
    {
        easyRsaPath = Path.GetFullPath(easyRsaPath);
        var serverCertificate = await easyRsaService.RevokeCertificateAsync(
            easyRsaPath, commonName, cancellationToken);
        
        logger.LogInformation("RevokeCertificate result: {Message} " +
                               "for CertName: {CommonName}", serverCertificate.Message, commonName);
        string revokedFilePath = MoveRevokedOvpnFile(ovpnFileName, ovpnFilePath, easyRsaPath);
        logger.LogInformation("Successfully moved revoked .ovpn file to: {RevokedFilePath}", revokedFilePath);

        logger.LogInformation("Updated database for revoked certificate: {CommonName}, " +
                               "External ID: {ExternalId}", commonName, 0);

        return new OvpnFileMetadata()
        {
            CommonName = commonName,
            FileName = ovpnFileName,
            FilePath = revokedFilePath,
            CertFilePath = serverCertificate.CertificatePath,
            KeyFilePath = serverCertificate.KeyPath,
        };
    }

    public async Task<OvpnFileDownload> GetOvpnFile(
        string fileName,
        string filePath,
        string? commonName,
        CancellationToken cancellationToken)
    {
        filePath = Path.GetFullPath(filePath);

        if (!File.Exists(filePath))
        {
            if (string.IsNullOrWhiteSpace(commonName))
                throw new FileNotFoundException($"File {filePath} does not exist", filePath);

            await issuanceTracker.WaitUntilReadyAsync(commonName, filePath, cancellationToken);
        }

        try
        {
            var content = await File.ReadAllBytesAsync(filePath, cancellationToken);
            return new OvpnFileDownload
            {
                CommonName = commonName ?? string.Empty,
                FileName = fileName,
                Content = content
            };
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not TimeoutException and not OperationCanceledException)
        {
            throw new Exception($"Error reading OVPN file {filePath}: {ex.Message}", ex);
        }
    }
    
    private string MoveRevokedOvpnFile(string ovpnFileName, string ovpnFilePath, string easyRsaPath)
    {
        var revokedOvpnFilesDirPath = Path.Combine(easyRsaPath, "pki", "revoked", "ovpn_files");
    
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var uniqueFileName = 
            $"{Path.GetFileNameWithoutExtension(ovpnFileName)}" +
            $"_{timestamp}" +
            $"{Path.GetExtension(ovpnFileName)}";

        var revokedFilePath = Path.Combine(revokedOvpnFilesDirPath, uniqueFileName);

        Directory.CreateDirectory(revokedOvpnFilesDirPath);

        if (File.Exists(ovpnFilePath))
        {
            File.Move(ovpnFilePath, revokedFilePath);
            logger.LogInformation($"Moved .ovpn file to revoked folder: {revokedFilePath}");
        }
        else
        {
            logger.LogWarning($".ovpn file not found for moving: {ovpnFilePath}");
        }

        return revokedFilePath;
    }
    
    private async Task<string> ReadPemContentAsync(string filePath, CancellationToken cancellationToken)
    {
        filePath = Path.GetFullPath(filePath);
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        return string.Join(Environment.NewLine, lines
            .SkipWhile(line => !line.StartsWith("-----BEGIN CERTIFICATE-----"))
            .TakeWhile(line => !line.StartsWith("-----END CERTIFICATE-----"))
            .Append("-----END CERTIFICATE-----"));
    }
    
    private string GenerateOvpnFile(
        string configTemplate,
        string friendlyΝame,
        string serverIp,
        int serverPort,
        string caCert,
        string clientCert,
        string clientKey,
        string tlsAuthKey)
    {
        if (string.IsNullOrWhiteSpace(configTemplate))
            throw new ArgumentNullException(nameof(configTemplate));
        if (string.IsNullOrWhiteSpace(serverIp))
            throw new ArgumentNullException(nameof(serverIp));
        if (string.IsNullOrWhiteSpace(caCert))
            throw new ArgumentNullException(nameof(caCert));
        if (string.IsNullOrWhiteSpace(clientCert))
            throw new ArgumentNullException(nameof(clientCert));
        if (string.IsNullOrWhiteSpace(clientKey))
            throw new ArgumentNullException(nameof(clientKey));
        if (string.IsNullOrWhiteSpace(tlsAuthKey))
            throw new ArgumentNullException(nameof(tlsAuthKey));

        var content = configTemplate
            .Replace("{{friendly_name}}", friendlyΝame)
            .Replace("{{server_ip}}", serverIp)
            .Replace("{{server_port}}", serverPort.ToString())
            .Replace("{{ca_cert}}", caCert)
            .Replace("{{client_cert}}", clientCert)
            .Replace("{{client_key}}", clientKey)
            .Replace("{{tls_auth_key}}", tlsAuthKey);

        // Align client directives with live node env (same defaults as entrypoint.sh).
        return OvpnClientTemplateSync.Apply(content, OvpnNodeClientSettings.FromConfiguration(configuration));
    }
}
