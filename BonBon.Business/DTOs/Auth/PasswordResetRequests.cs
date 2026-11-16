using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>Request body cho POST /auth/forgot-password — gửi OTP về Gmail.</summary>
public sealed class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = default!;
}

/// <summary>Request body cho POST /auth/reset-password — xác thực OTP và đặt mật khẩu mới.</summary>
public sealed class ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = default!;

    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Mã OTP gồm 6 chữ số.")]
    public string Otp { get; init; } = default!;

    [Required, MinLength(6, ErrorMessage = "Mật khẩu mới cần có ít nhất 6 ký tự.")]
    [MaxLength(128)]
    public string NewPassword { get; init; } = default!;
}
