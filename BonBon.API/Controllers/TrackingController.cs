using System.Security.Claims;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/bookings/{bookingId:guid}/tracking")]
[Authorize]
public sealed class TrackingController(ITrackingService trackingService) : ControllerBase
{
    private readonly ITrackingService _trackingService = trackingService;

    [HttpPut("meeting-points")]
    public async Task<IActionResult> SetMeetingPoints(Guid bookingId, SetMeetingPointsRequest request, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.SetMeetingPointsAsync(accountId.Value, bookingId, request, ct);
        return result.IsSuccess ? NoContent() : Problem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPut("device")]
    public async Task<IActionResult> SetDevice(Guid bookingId, SetTrackerDeviceRequest request, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.SetDeviceAsync(accountId.Value, bookingId, request.DeviceId, ct);
        return result.IsSuccess ? NoContent() : Problem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot(Guid bookingId, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.GetSnapshotAsync(accountId.Value, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : Problem(result.ErrorCode!, result.ErrorMessage!);
    }

    private Guid? GetAccountId() =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private IActionResult Problem(string code, string detail)
    {
        var status = code == "BOOKING_NOT_FOUND" ? StatusCodes.Status404NotFound
            : code is "TRACKING_NOT_ACTIVE" or "TRACKER_NOT_CONFIGURED" or "MEETING_POINT_NOT_CONFIGURED" or "MEETING_POINTS_LOCKED"
                ? StatusCodes.Status409Conflict
                : code == "POSITION_NOT_AVAILABLE" ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status400BadRequest;
        return StatusCode(status, new ProblemDetails { Title = code, Detail = detail, Status = status });
    }

    public sealed record SetTrackerDeviceRequest(long DeviceId);
}
