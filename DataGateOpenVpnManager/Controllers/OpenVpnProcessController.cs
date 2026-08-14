using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OpenVpnProcess.Responses;
using DataGateMonitor.SharedModels.Responses;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataGateOpenVpnManager.Controllers;

/// <summary>
/// Controls the OpenVPN daemon process inside this container (start / restart / kill).
/// Requires microservice JWT. Disconnects all VPN clients on kill/restart.
/// </summary>
[ApiController]
[Route("api/openvpn")]
public class OpenVpnProcessController(IOpenVpnProcessService processService) : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType(typeof(ApiResponse<OpenVpnProcessStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OpenVpnProcessStatusResponse>>> Status(
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await processService.GetStatusAsync(cancellationToken);
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
    }

    [HttpPost("start")]
    [ProducesResponseType(typeof(ApiResponse<OpenVpnProcessStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OpenVpnProcessStatusResponse>>> Start(
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await processService.StartAsync(cancellationToken);
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
    }

    [HttpPost("restart")]
    [ProducesResponseType(typeof(ApiResponse<OpenVpnProcessStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OpenVpnProcessStatusResponse>>> Restart(
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await processService.RestartAsync(cancellationToken);
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
    }

    [HttpPost("kill")]
    [ProducesResponseType(typeof(ApiResponse<OpenVpnProcessStatusResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OpenVpnProcessStatusResponse>>> Kill(
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await processService.KillAsync(cancellationToken);
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
    }
}
