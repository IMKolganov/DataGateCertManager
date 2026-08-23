using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.Info;
using DataGateMonitor.SharedModels.Responses;
using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Services.Interfaces;

namespace DataGateOpenVpnManager.Controllers;

[ApiController]
[Route("api/info")]
public class IndexController(
    IConfiguration config,
    IWebHostEnvironment env,
    ILogger<IndexController> logger,
    IExternalIpAddressService externalIpAddressService)
    : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<RootOpenVpnInfoResponse>>> Get(CancellationToken cancellationToken)
    {
        try
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown version";
            string? publicIp = null;
            try
            {
                publicIp = await externalIpAddressService.GetPublicIpAddressAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to resolve PublicIp for /api/info");
            }

            var clientSettings = OvpnNodeClientSettings.FromConfiguration(config);

            var response = new RootOpenVpnInfoResponse
            {
                Version = version,
                Environment = env.EnvironmentName,
                Application = "DataGateOpenVpnManager",
                Description = "This service manages OpenVPN certificates and provides a JSON API for operations like create/revoke.",
                PublicIp = publicIp,
                Config = new ConfigInfoResponse
                {
                    Dns1 = config["DNS1"],
                    Dns2 = config["DNS2"],
                    VpnSubnet = config["VPN_SUBNET"],
                    VpnNetmask = config["VPN_NETMASK"],
                    EasyRsaPath = config["EASY_RSA_PATH"],
                    DataDir = config["DATA_DIR"],
                    Port = config["PORT"],
                    ApiPort = config["API_PORT"],
                    Proto = clientSettings.Proto ?? config["PROTO"],
                    Cipher = clientSettings.Cipher,
                    DataCiphers = clientSettings.DataCiphers,
                    Dco = config["DCO"],
                    Auth = clientSettings.Auth,
                    TlsVersionMin = clientSettings.TlsVersionMin,
                    MssFix = config["MSSFIX"],
                    ClientVerb = clientSettings.ClientVerb,
                    OpenVpnManagement = new OpenVpnManagementInfoResponse
                    {
                        Host = config["OpenVpnManagement:Host"],
                        Port = config["OpenVpnManagement:Port"]
                    },
                    BackendBaseUrl = config["BACKEND__BASEURL"]
                }
            };
            return Ok(ApiResponse<RootOpenVpnInfoResponse>.SuccessResponse(response));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting info");
            return BadRequest(ApiResponse<RootOpenVpnInfoResponse>.ErrorResponse(ex.Message));
        }
    }
}
