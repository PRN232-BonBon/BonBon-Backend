using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>Request body cho POST /auth/login.</summary>
public sealed class LoginRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = default!;

    [Required]
    [MinLength(6)]
    [MaxLength(128)]
    public string Password { get; init; } = default!;

    /// <summary>Thông tin thiết bị để gắn vào refresh token (tuỳ chọn).</summary>
    [MaxLength(512)]
    public string? DeviceInfo { get; init; }
}
