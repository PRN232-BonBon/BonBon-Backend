using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>Request body cho POST /auth/register (tự đăng ký — luôn tạo Customer role).</summary>
public sealed class RegisterRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = default!;

    [Required]
    [MinLength(6)]
    [MaxLength(128)]
    public string Password { get; init; } = default!;

    [Required]
    [MinLength(2)]
    [MaxLength(150)]
    public string FullName { get; init; } = default!;

    [MaxLength(150)]
    public string? DisplayName { get; init; }

    [Phone]
    [MaxLength(30)]
    public string? Phone { get; init; }

    /// <summary>Thông tin thiết bị để gắn vào refresh token (tuỳ chọn).</summary>
    [MaxLength(512)]
    public string? DeviceInfo { get; init; }
}
