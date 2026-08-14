using DataGateOpenVpnManager.Helpers;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Requests;
using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OvpnFile.Responses;
using DataGateMonitor.SharedModels.Responses;

namespace DataGateOpenVpnManager.Controllers;

[ApiController]
[Route("api/ovpn-files")]
public class OvpnFileController(
    IOvpnFileService ovpnFileService,
    IEasyRsaPathResolver easyRsaPathResolver,
    ILogger<OvpnFileController> logger)
    : ControllerBase
{
    [HttpPost("add")]
    public async Task<ActionResult<ApiResponse<OvpnFileMetadata>>> AddOvpnFile([FromBody] GenerateOvpnFileRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var mainPath = easyRsaPathResolver.GetEasyRsaPath();

            if (string.IsNullOrEmpty(request.CommonName) || string.IsNullOrEmpty(request.ConfigTemplate))//todo: fix
            {
                throw new NullReferenceException("Common name and config template are required");
            }

            var result = await ovpnFileService.AddOvpnFile(
                mainPath,
                request.CommonName,
                request.FriendlyΝame,
                request.ConfigTemplate,
                request.ServerIp,
                request.ServerPort,
                cancellationToken,
                request.IssuedTo,
                request.OvpnFileExpireDays);

            return Ok(ApiResponse<OvpnFileMetadata>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error added new ovpn file for {CommonName}", request.CommonName);
            return BadRequest(ApiResponse<OvpnFileMetadata>.ErrorResponse(ex.Message));
        }
    }

    [HttpPost("revoke")]
    public async Task<ActionResult<ApiResponse<OvpnFileMetadata>>> RevokeOvpnFile([FromBody] RevokeOvpnFileRequest request, 
        CancellationToken cancellationToken)
    {
        try
        {
            var mainPath = easyRsaPathResolver.GetEasyRsaPath();

            var result = await ovpnFileService.RevokeOvpnFile(
                mainPath,
                request.CommonName,
                request.OvpnFileName,
                request.OvpnFilePath,
                cancellationToken);

            return Ok(ApiResponse<OvpnFileMetadata>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error revoke ovpn file for {CommonName}", request.CommonName);
            return BadRequest(ApiResponse<OvpnFileMetadata>.ErrorResponse(ex.Message));
        }
    }

    [HttpPost("download")]
    public async Task<ActionResult<ApiResponse<OvpnFileDownload>>> DownloadOvpnFile([FromBody] DownloadOvpnFileRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ovpnFileService.GetOvpnFile(
                request.FileName,
                request.FilePath,
                request.CommonName,
                cancellationToken);

            return Ok(ApiResponse<OvpnFileDownload>.SuccessResponse(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error download ovpn file for {CommonName}", request.CommonName);
            return BadRequest(ApiResponse<OvpnFileDownload>.ErrorResponse(ex.Message));
        }
    }
}

