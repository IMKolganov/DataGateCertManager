
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Responses;

namespace DataGateOpenVpnManager.Services.Interfaces;

public interface IOvpnFileService
{
    Task<OvpnFileMetadata> AddOvpnFile(string easyRsaPath, string commonName, string friendlyΝame,string configTemplate, 
        string serverIp, int serverPort, CancellationToken cancellationToken, 
        string issuedTo = "openVpnClient", int certExpireDays = 365);

    Task<OvpnFileMetadata?> RevokeOvpnFile(string easyRsaPath, string commonName,
        string ovpnFileName, string ovpnFilePath, CancellationToken cancellationToken);
    Task<OvpnFileDownload> GetOvpnFile(
        string fileName,
        string filePath,
        string? commonName,
        CancellationToken cancellationToken);
}