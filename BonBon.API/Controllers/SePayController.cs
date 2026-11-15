using System.Security.Claims;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/payments/sepay")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SePayController(ISePayIpnHandler ipn, ISePayReconciliationService reconciliation,
    ISePayPaymentRepository repository, IOptions<SePayOptions> config) : ControllerBase
{
    [HttpPost("ipn")]
    [AllowAnonymous]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Ipn([FromBody] SePayIpn body, CancellationToken ct)
    {
        Response.Headers["X-BonBon-SePay-Endpoint"] = "ipn-v1";
        var keys = Request.Headers["X-Secret-Key"];
        var status = await ipn.HandleAsync(keys.Count == 1 ? keys.ToString() : "", body, ct);
        return StatusCode(status, new { success = status == 200 });
    }

    [HttpGet("return")]
    [AllowAnonymous]
    public IActionResult Return([FromQuery] Guid? paymentId)
    {
        Response.Headers["X-BonBon-SePay-Endpoint"] = "return-v1";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        if (!config.Value.IsValid()) return StatusCode(503, new ProblemDetails { Title = "PAYMENT_CONFIGURATION_INVALID", Status = 503 });
        // The ID is only a navigation hint. GET Payment still requires the owning customer's JWT.
        // No return/result/status parameter is used as payment evidence, and no write service is called.
        var destination = config.Value.FrontendResultUrl + "?provider=SEPAY_SANDBOX";
        if (paymentId is { } id && id != Guid.Empty && Request.Query["paymentId"].Count == 1)
            destination += "&paymentId=" + id.ToString("D");
        else destination += "&returnError=invalid";
        return Redirect(destination);
    }

    [HttpPost("{paymentId:guid}/reconcile")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Reconcile(Guid paymentId, CancellationToken ct)
    {
        if (!config.Value.Enabled) return StatusCode(503, new { code = "PAYMENT_DISABLED" });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var admin)) return Unauthorized();
        try {
            if (!await repository.IsActiveAdminAsync(admin, ct)) return Forbid();
            var status = await reconciliation.ReconcileAsync(paymentId.ToString("N"), ct);
            return StatusCode(status, new { accepted = status == 200, message = "Đọc lại GET Payment để biết trạng thái và requiresReconciliation." });
        } catch (NpgsqlException) { return StatusCode(503, new { code = "PAYMENT_TEMPORARILY_UNAVAILABLE" }); }
    }
}
