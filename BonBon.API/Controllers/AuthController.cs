using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BonBon.API.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController(IAuthService authService) : ControllerBase
{
    private readonly IAuthService _authService = authService;

    // -------------------------------------------------------------------------
    // POST /api/auth/register
    // -------------------------------------------------------------------------
    /// <summary>Đăng ký tài khoản mới (tự động gán role Customer).</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken ct)
    {
        var result = await _authService.RegisterAsync(request, ct);

        if (!result.IsSuccess)
            return result.ErrorCode switch
            {
                "EMAIL_TAKEN" => Conflict(new ProblemDetails
                {
                    Title  = "Email đã được sử dụng.",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status409Conflict,
                }),
                _ => BadRequest(new ProblemDetails
                {
                    Title  = result.ErrorCode,
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest,
                }),
            };

        return CreatedAtAction(nameof(Register), result.Data);
    }

    // -------------------------------------------------------------------------
    // POST /api/auth/login
    // -------------------------------------------------------------------------
    /// <summary>Đăng nhập bằng email + mật khẩu.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken ct)
    {
        var result = await _authService.LoginAsync(request, ct);

        if (!result.IsSuccess)
            return result.ErrorCode switch
            {
                "INVALID_CREDENTIALS" => Unauthorized(new ProblemDetails
                {
                    Title  = "Thông tin đăng nhập không đúng.",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status401Unauthorized,
                }),
                "ACCOUNT_INACTIVE" => Forbid(),
                _ => BadRequest(new ProblemDetails
                {
                    Title  = result.ErrorCode,
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest,
                }),
            };

        return Ok(result.Data);
    }

    // -------------------------------------------------------------------------
    // POST /api/auth/google-login
    // -------------------------------------------------------------------------
    /// <summary>Đăng nhập / đăng ký qua Google ID Token (từ Google Sign-In SDK phía client).</summary>
    [HttpPost("google-login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GoogleLogin(
        [FromBody] GoogleLoginRequest request,
        CancellationToken ct)
    {
        var result = await _authService.GoogleLoginAsync(request, ct);

        if (!result.IsSuccess)
            return result.ErrorCode switch
            {
                "INVALID_GOOGLE_TOKEN" => Unauthorized(new ProblemDetails
                {
                    Title  = "Google token không hợp lệ.",
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status401Unauthorized,
                }),
                "ACCOUNT_INACTIVE" => Forbid(),
                _ => BadRequest(new ProblemDetails
                {
                    Title  = result.ErrorCode,
                    Detail = result.ErrorMessage,
                    Status = StatusCodes.Status400BadRequest,
                }),
            };

        return Ok(result.Data);
    }

    // -------------------------------------------------------------------------
    // POST /api/auth/refresh
    // -------------------------------------------------------------------------
    /// <summary>Cấp lại access token bằng refresh token hợp lệ (token rotation).</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshTokenRequest request,
        CancellationToken ct)
    {
        var result = await _authService.RefreshAsync(request, ct);

        if (!result.IsSuccess)
            return Unauthorized(new ProblemDetails
            {
                Title  = result.ErrorCode,
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status401Unauthorized,
            });

        return Ok(result.Data);
    }

    // -------------------------------------------------------------------------
    // POST /api/auth/logout
    // -------------------------------------------------------------------------
    /// <summary>Đăng xuất — revoke refresh token hiện tại. Yêu cầu xác thực.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout(
        [FromBody] RefreshTokenRequest request,
        CancellationToken ct)
    {
        var result = await _authService.LogoutAsync(request.RefreshToken, ct);

        if (!result.IsSuccess)
            return BadRequest(new ProblemDetails
            {
                Title  = result.ErrorCode,
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status400BadRequest,
            });

        return NoContent();
    }

    // -------------------------------------------------------------------------
    // POST /api/auth/logout-all
    // -------------------------------------------------------------------------
    /// <summary>Đăng xuất toàn bộ thiết bị — revoke tất cả refresh token của account.</summary>
    [HttpPost("logout-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        var accountId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("AccountId claim missing."));

        await _authService.LogoutAllDevicesAsync(accountId, ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
        CancellationToken ct)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(claim, out var accountId)) return Unauthorized();

        var result = await _authService.ChangePasswordAsync(accountId, request, ct);
        if (!result.IsSuccess)
            return BadRequest(new ProblemDetails
            {
                Title = result.ErrorCode,
                Detail = result.ErrorMessage,
                Status = StatusCodes.Status400BadRequest,
            });

        return NoContent();
    }
}
