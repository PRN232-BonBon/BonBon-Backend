namespace BonBon.Business.Services.Interfaces;

/// <summary>Gửi email giao dịch (OTP đặt lại mật khẩu). Triển khai cụ thể
/// (Gmail SMTP) nằm ở tầng API để tận dụng logging + hosting environment.</summary>
public interface IEmailSender
{
    Task SendPasswordResetOtpAsync(string toEmail, string otp, int expiryMinutes, CancellationToken ct = default);
}
