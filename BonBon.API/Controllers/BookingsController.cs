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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Quote([FromBody] BookingQuoteRequest request, CancellationToken ct)
    {
        var result = await _bookingService.QuoteAsync(request, ct);
        return result.IsSuccess ? Ok(result.Data) : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("availability")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
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
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest request, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.CreateAsync(accountId, request, ct);
        if (!result.IsSuccess) return BookingProblem(result.ErrorCode!, result.ErrorMessage!);

        return Created($"/api/bookings/{result.Data!.BookingId}", result.Data);
    }

    [HttpGet("mine")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<CustomerBookingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.GetMineAsync(accountId, ct);
        return Ok(result.Data);
    }

    [HttpGet("owner")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerBookingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> OwnerBookings(CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.GetOwnerBookingsAsync(accountId, ct);
        return Ok(result.Data);
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(BookingDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetById([FromRoute] Guid id, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var isAdmin = User.IsInRole("Admin");
        var result = await _bookingService.GetDetailAsync(id, accountId, isAdmin, ct);
        return result.IsSuccess ? Ok(result.Data) : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel([FromRoute] Guid id, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.CancelAsync(id, accountId, ct);
        return result.IsSuccess
            ? Ok(new { message = "Đã hủy đơn đặt xe thành công." })
            : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Approve([FromRoute] Guid id, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.ApproveAsync(id, accountId, ct);
        return result.IsSuccess
            ? Ok(new { message = "Đã duyệt đơn đặt xe thành công. Chờ khách hàng thanh toán." })
            : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject([FromRoute] Guid id, CancellationToken ct)
    {
        if (!TryGetAccountId(out var accountId)) return Unauthorized();

        var result = await _bookingService.RejectAsync(id, accountId, ct);
        return result.IsSuccess
            ? Ok(new { message = "Đã từ chối đơn đặt xe thành công." })
            : BookingProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    private bool TryGetAccountId(out Guid accountId)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(claim, out accountId);
    }

    private ObjectResult BookingProblem(string code, string detail)
    {
        var statusCode = code switch
        {
            "BOOKING_NOT_FOUND" => StatusCodes.Status404NotFound,
            "FORBIDDEN" => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };

        return StatusCode(statusCode, new ProblemDetails
        {
            Title = code,
            Detail = detail,
            Status = statusCode,
        });
    }
}
