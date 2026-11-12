using System.Security.Claims;
using BonBon.API.Infrastructure;
using BonBon.Business.DTOs.Tracking;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/bookings/{bookingId:guid}/tracking")]
[Produces("application/json")]
[Authorize]
public sealed class TrackingController(
    ITrackingService trackingService,
    IHubContext<TrackingHub> hub) : ControllerBase
{
    private readonly ITrackingService _trackingService = trackingService;
    private readonly IHubContext<TrackingHub> _hub = hub;

    [HttpPut("meeting-points")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SetMeetingPoints(Guid bookingId, SetMeetingPointsRequest request,
        CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.SetMeetingPointsAsync(accountId.Value, bookingId, request, ct);
        return result.IsSuccess ? NoContent() : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    /// <summary>Điện thoại của người thuê gửi vị trí GPS; chủ xe nhận qua SignalR.</summary>
    [HttpPost("positions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReportPosition(Guid bookingId, ReportTrackingPositionRequest request,
        CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();

        var result = await _trackingService.ReportPositionAsync(accountId.Value, bookingId, request, ct);
        if (!result.IsSuccess) return TrackingProblem(result.ErrorCode!, result.ErrorMessage!);

        if (result.Data is not null)
            await _hub.Clients.Group(TrackingHub.GroupName(bookingId))
                .SendAsync("PositionUpdated", result.Data, ct);

        return NoContent();
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(TrackingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(Guid bookingId, CancellationToken ct)
    {
        var accountId = GetAccountId();
        if (accountId is null) return Unauthorized();
        var result = await _trackingService.GetStatusAsync(accountId.Value, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : TrackingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(TrackingSnapshotResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
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
            "FORBIDDEN" => StatusCodes.Status403Forbidden,
            "POSITION_NOT_AVAILABLE" => StatusCodes.Status503ServiceUnavailable,
            "TRACKING_NOT_ACTIVE" or "MEETING_POINT_NOT_CONFIGURED" or "MEETING_POINTS_LOCKED"
                => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, new ProblemDetails { Title = code, Detail = detail, Status = status });
    }
}
