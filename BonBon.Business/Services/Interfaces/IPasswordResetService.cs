using BonBon.Business.Common;
using BonBon.Business.DTOs.Auth;

namespace BonBon.Business.Services.Interfaces;

/// <summary>Quên mật khẩu bằng OTP gửi qua Gmail.</summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Gửi OTP về email. Luôn trả Success (kể cả email không tồn tại)
    /// để chống liệt kê tài khoản.
    /// </summary>
    Task<ServiceResult> RequestAsync(ForgotPasswordRequest request, CancellationToken ct = default);

    /// <summary>Xác thực OTP và đặt mật khẩu mới (đồng thời revoke mọi refresh token).</summary>
    Task<ServiceResult> ResetAsync(ResetPasswordRequest request, CancellationToken ct = default);
}
