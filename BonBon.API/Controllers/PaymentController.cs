using System.Security.Claims;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PaymentController(IPaymentService payments, IOptions<PaymentReturnOptions> returnOptions) : ControllerBase
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
        var result = await payments.CreateAsync(customer, request, idempotencyKey ?? "", ip?.ToString() ?? "", ct);
        return result.IsSuccess ? Ok(result.Data) : PaymentProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("{paymentId:guid}")]
    [Authorize(Roles = "Customer")]
    [ProducesResponseType(typeof(PaymentStatusResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid paymentId, CancellationToken ct)
    {
        if (!TryAccount(out var customer)) return Unauthorized();
        var result = await payments.GetAsync(customer, paymentId, ct);
        return result.IsSuccess ? Ok(result.Data) : PaymentProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("vnpay/ipn")]
    [AllowAnonymous]
    public async Task<IActionResult> Ipn(CancellationToken ct)
    {
        if (!TryParameters(out var parameters)) return Ok(new VnPayIpnResponse("97", "Invalid parameters"));
        return Ok(await payments.HandleIpnAsync(parameters, ct));
    }

    [HttpGet("vnpay/return")]
    [AllowAnonymous]
    public async Task<IActionResult> Return(CancellationToken ct)
    {
        if (!TryParameters(out var parameters)) return PaymentProblem("PAYMENT_RETURN_INVALID", "Tham số không hợp lệ.");
        var result = await payments.HandleReturnAsync(parameters, ct);
        return result.IsSuccess ? Ok(result.Data) : PaymentProblem(result.ErrorCode!, result.ErrorMessage!);
    }

    [HttpGet("vnpay/return/redirect")]
    [AllowAnonymous]
    public async Task<IActionResult> ReturnRedirect(CancellationToken ct)
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!returnOptions.Value.TryGetDestination(out var destination))
            return PaymentProblem("PAYMENT_CONFIGURATION_INVALID", "Chưa cấu hình trang kết quả thanh toán.");

        // Only a verified, database-resolved ID crosses this redirect. No provider status
        // or signature is forwarded, and this path never calls the IPN/write handler.
        if (!TryParameters(out var parameters)) return Redirect(destination.AbsoluteUri + "?returnError=invalid");
        var result = await payments.HandleReturnAsync(parameters, ct);
        if (result.IsSuccess)
            return Redirect(destination.AbsoluteUri + "?paymentId=" + result.Data!.PaymentId.ToString("D"));
        var error = result.ErrorCode is "PAYMENT_DISABLED" or "PAYMENT_CONFIGURATION_INVALID" or
            "PAYMENT_SCHEMA_UNAVAILABLE" or "PAYMENT_TEMPORARILY_UNAVAILABLE" ? "unavailable" : "invalid";
        return Redirect(destination.AbsoluteUri + "?returnError=" + error);
    }

    private bool TryAccount(out Guid id) => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out id) && id != Guid.Empty;
    private bool TryParameters(out IReadOnlyDictionary<string, string> parameters)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        parameters = result;
        if ((Request.QueryString.Value?.Length ?? 0) > 8192 || Request.Query.Count > 40) return false;
        foreach (var pair in Request.Query)
        {
            if (pair.Value.Count != 1) return false;
            if (pair.Key.StartsWith("vnp_", StringComparison.Ordinal)) result.Add(pair.Key, pair.Value.ToString());
        }
        return true;
    }
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
