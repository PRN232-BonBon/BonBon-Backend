using System.Security.Claims;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentController(ISePayCheckoutService sepay) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Customer")]
    [ProducesResponseType(typeof(PaymentCheckoutResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (!TryAccount(out var customer)) return Unauthorized();
        if (Request.Headers["Idempotency-Key"].Count != 1) return PaymentProblem("PAYMENT_REQUEST_INVALID", "Cần đúng một Idempotency-Key.");
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
        if (request.Provider == "SEPAY_SANDBOX") {
            var checkout = await sepay.CreateAsync(customer, request, idempotencyKey ?? "", ip?.ToString() ?? "", ct);
            return checkout.IsSuccess ? Ok(checkout.Data) : PaymentProblem(checkout.ErrorCode!, checkout.ErrorMessage!);
        }
        return PaymentProblem("PAYMENT_REQUEST_INVALID", "Provider không được hỗ trợ.");
    }

    [HttpGet("{paymentId:guid}")]
    [Authorize(Roles = "Customer")]
    [ProducesResponseType(typeof(PaymentStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid paymentId, CancellationToken ct, [FromQuery] string provider = "SEPAY_SANDBOX")
    {
        if (!TryAccount(out var customer)) return Unauthorized();
        if (provider == "SEPAY_SANDBOX") {
            var payment = await sepay.GetAsync(customer, paymentId, ct);
            return payment.IsSuccess ? Ok(payment.Data) : PaymentProblem(payment.ErrorCode!, payment.ErrorMessage!);
        }
        return PaymentProblem("PAYMENT_REQUEST_INVALID", "Provider không được hỗ trợ.");
    }

    private bool TryAccount(out Guid id) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out id) && id != Guid.Empty;
    private ObjectResult PaymentProblem(string code, string detail)
    {
        var status = code switch
        {
            "BOOKING_NOT_FOUND" or "PAYMENT_NOT_FOUND" => 404,
            "PAYMENT_FORBIDDEN" or "PAYMENT_ACTOR_INACTIVE" => 403,
            "PAYMENT_REQUEST_INVALID" or "PAYMENT_RETURN_INVALID" => 400,
            "PAYMENT_DISABLED" or "PAYMENT_CONFIGURATION_INVALID" or "PAYMENT_SCHEMA_UNAVAILABLE" or "PAYMENT_TEMPORARILY_UNAVAILABLE" => 503,
            _ => 409,
        };
        return StatusCode(status, new ProblemDetails { Title = code, Detail = detail, Status = status });
    }
}
