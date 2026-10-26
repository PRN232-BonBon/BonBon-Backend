using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BonBon.API.Controllers;

public sealed class AdminLoginRequest
{
    [Required, MaxLength(254)] public string Username { get; init; } = "";
    [Required, MinLength(6), MaxLength(128)] public string Password { get; init; } = "";
}

public sealed class VehicleReviewRequest
{
    [Required] public string Decision { get; init; } = "";
    [MaxLength(1000)] public string? Reason { get; init; }
}

[ApiController]
[Route("api/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(IAuthService auth, IAdminRepository repository, IAccountRepository accounts) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("admin-login")]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken ct)
    {
        // Public customer registration cannot grant Admin membership.
        var account = await accounts.GetByEmailAsync(request.Username, ct);
        if (account is null || !await repository.IsActiveAdminAsync(account.AccountId, ct))
            return Unauthorized(new ProblemDetails { Detail = "Tên đăng nhập hoặc mật khẩu không đúng." });
        var result = await auth.LoginAsync(new LoginRequest { Email = request.Username, Password = request.Password, DeviceInfo = "BonBon Admin" }, ct);
        if (!result.IsSuccess)
            return Unauthorized(new ProblemDetails { Detail = "Tên đăng nhập hoặc mật khẩu không đúng." });
        return Ok(result.Data);
    }

    [HttpGet("vehicles")]
    public async Task<IActionResult> Vehicles(CancellationToken ct)
    {
        if (!await IsActiveAdmin(ct)) return Forbid();
        return Ok(await repository.GetVehiclesAsync(false, ct));
    }

    [HttpPost("vehicles/{vehicleId:guid}/review")]
    public async Task<IActionResult> Review(Guid vehicleId, VehicleReviewRequest request, CancellationToken ct)
    {
        if (!await IsActiveAdmin(ct)) return Forbid();
        if (request.Decision is not ("approve" or "reject"))
            return BadRequest(new ProblemDetails { Detail = "Thao tác duyệt xe không hợp lệ." });
        var reason = request.Reason?.Trim();
        if (request.Decision == "reject" && string.IsNullOrWhiteSpace(reason))
            return BadRequest(new ProblemDetails { Detail = "Vui lòng nhập lý do từ chối." });
        var adminId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await repository.ReviewAsync(adminId, vehicleId, request.Decision == "approve", request.Decision == "reject" ? reason : null, ct);
        return result switch
        {
            "OK" => Ok(new { status = request.Decision == "approve" ? "Available" : "Rejected" }),
            "NOT_FOUND" => NotFound(),
            "ALREADY_REVIEWED" => Conflict(new ProblemDetails { Detail = "Xe đã được xử lý. Vui lòng tải lại danh sách." }),
            "OWNER_INACTIVE" => Conflict(new ProblemDetails { Detail = "Tài khoản hoặc quyền chủ xe đang bị khóa." }),
            _ => BadRequest(new ProblemDetails { Detail = "Xe cần có ít nhất một ảnh trước khi được duyệt." }),
        };
    }

    private Task<bool> IsActiveAdmin(CancellationToken ct) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? repository.IsActiveAdminAsync(id, ct) : Task.FromResult(false);
}
