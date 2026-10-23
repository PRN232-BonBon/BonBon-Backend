using BonBon.Business.DTOs.Bookings;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/bookings")]
[Produces("application/json")]
public sealed class BookingsController(IBookingService bookingService) : ControllerBase
{
    private readonly IBookingService _bookingService = bookingService;

    [HttpPost("{bookingId:guid}/approve")]
    [Authorize(Roles = "VehicleOwner")]
    [ProducesResponseType(typeof(BookingDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid bookingId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId) || accountId == Guid.Empty)
            return Unauthorized();
        var result = await _bookingService.ApproveAsync(accountId, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : DecisionProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("{bookingId:guid}/reject")]
    [Authorize(Roles = "VehicleOwner")]
    [ProducesResponseType(typeof(BookingDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid bookingId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var accountId) || accountId == Guid.Empty)
            return Unauthorized();
        var result = await _bookingService.RejectAsync(accountId, bookingId, ct);
        return result.IsSuccess ? Ok(result.Data) : DecisionProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    private ObjectResult DecisionProblem(string code, string detail)
    {
        var status = code switch
        {
            "BOOKING_ID_INVALID" => StatusCodes.Status400BadRequest,
            "BOOKING_NOT_FOUND" => StatusCodes.Status404NotFound,
            "BOOKING_NOT_OWNER" or "OWNER_INACTIVE" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status409Conflict,
        };
        return StatusCode(status, new ProblemDetails { Title = code, Detail = detail, Status = status });
    }

    [HttpPost("validate-window")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RentalWindowResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult ValidateWindow([FromBody] RentalWindowRequest request)
    {
        var result = _bookingService.ValidateWindow(request);
        return result.IsSuccess ? Ok(result.Data) : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("quote")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(BookingQuoteResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Quote([FromBody] BookingQuoteRequest request, CancellationToken ct)
    {
        var result = await _bookingService.QuoteAsync(request, ct);
        return result.IsSuccess ? Ok(result.Data) : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("availability")]
    [AllowAnonymous]
    public async Task<IActionResult> Availability(
        [FromQuery] Guid vehicleId,
        [FromQuery] string rentalType,
        [FromQuery] DateTimeOffset startAt,
        [FromQuery] DateTimeOffset endAt,
        CancellationToken ct)
    {
        var result = await _bookingService.IsAvailableAsync(vehicleId, new RentalWindowRequest
        {
            RentalType = rentalType,
            StartAt = startAt,
            EndAt = endAt,
        }, ct);
        return result.IsSuccess ? Ok(new { isAvailable = result.Data }) : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(BookingResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var accountId)) return Unauthorized();

        var result = await _bookingService.CreateAsync(accountId, request, ct);
        if (!result.IsSuccess) return BookingProblem(result.ErrorCode!, result.ErrorMessage!);

        return Created($"/api/bookings/{result.Data!.BookingId}", result.Data);
    }

    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<CustomerBookingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var accountId)) return Unauthorized();
        var result = await _bookingService.GetMineAsync(accountId, ct);
        return Ok(result.Data);
    }

    private BadRequestObjectResult BookingProblem(string code, string detail) =>
        BadRequest(new ProblemDetails
        {
            Title = code,
            Detail = detail,
            Status = StatusCodes.Status400BadRequest,
        });
}
