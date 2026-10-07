using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>
/// Request body cho POST /auth/google-login.
/// Client gửi ID Token nhận được từ Google Sign-In SDK.
/// </summary>
public sealed class GoogleLoginRequest
{
    [Required]
    public string IdToken { get; init; } = default!;

    /// <summary>Thông tin thiết bị để gắn vào refresh token (tuỳ chọn).</summary>
    [MaxLength(512)]
    public string? DeviceInfo { get; init; }
}
