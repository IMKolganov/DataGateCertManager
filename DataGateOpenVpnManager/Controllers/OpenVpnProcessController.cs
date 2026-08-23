using DataGateMonitor.SharedModels.DataGateOpenVpnManager.OpenVpnProcess.Responses;
using DataGateMonitor.SharedModels.Responses;
using DataGateOpenVpnManager.Services;
using DataGateOpenVpnManager.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DataGateOpenVpnManager.Controllers;

/// <summary>
/// Controls the OpenVPN daemon process inside this container (start / restart / kill).
/// Requires microservice JWT. Disconnects all VPN clients on kill/restart.
/// Concurrent mutate calls are rejected with 409 while another operation holds the gate.
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
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status, status.Message));
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperation(ex);
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
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status, status.Message));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperation(ex);
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
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status, status.Message));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperation(ex);
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
            return Ok(ApiResponse<OpenVpnProcessStatusResponse>.SuccessResponse(status, status.Message));
        }
        catch (InvalidOperationException ex)
        {
            return MapInvalidOperation(ex);
        }
    }

    private static ActionResult<ApiResponse<OpenVpnProcessStatusResponse>> MapInvalidOperation(
        InvalidOperationException ex)
    {
        var body = ApiResponse<OpenVpnProcessStatusResponse>.ErrorResponse(ex.Message);
        if (string.Equals(ex.Message, OpenVpnProcessService.BusyMessage, StringComparison.Ordinal))
            return new ConflictObjectResult(body);
        return new BadRequestObjectResult(body);
    }
}
