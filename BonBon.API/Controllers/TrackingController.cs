using System.Security.Claims;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/bookings/{bookingId:guid}/tracking")]
[Produces("application/json")]
[Authorize]
public sealed class TrackingController(ITrackingService trackingService) : ControllerBase
{
    private readonly ITrackingService _trackingService = trackingService;

    [HttpPut("meeting-points")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMeetingPoints(Guid bookingId, SetMeetingPointsRequest request, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.SetMeetingPointsAsync(accountId.Value, bookingId, request, ct);
        return result.IsSuccess ? NoContent() : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPut("device")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetDevice(Guid bookingId, SetTrackerDeviceRequest request, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.SetDeviceAsync(accountId.Value, bookingId, request.DeviceId, ct);
        return result.IsSuccess ? NoContent() : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(TrackingStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(Guid bookingId, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.GetStatusAsync(accountId.Value, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(TrackingSnapshotResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Snapshot(Guid bookingId, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.GetSnapshotAsync(accountId.Value, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    private Guid? GetAccountId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private IActionResult TrackingProblem(string code, string detail)
    {
        var status = code switch
        {
            "BOOKING_NOT_FOUND" => StatusCodes.Status404NotFound,
            "POSITION_NOT_AVAILABLE" => StatusCodes.Status503ServiceUnavailable,
            "TRACKING_NOT_ACTIVE" or "TRACKING_WINDOW_NOT_OPEN"
                or "TRACKER_NOT_CONFIGURED" or "MEETING_POINT_NOT_CONFIGURED"
                or "MEETING_POINTS_LOCKED" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, new ProblemDetails { Title = code, Detail = detail, Status = status });
    }
}
