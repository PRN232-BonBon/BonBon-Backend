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
