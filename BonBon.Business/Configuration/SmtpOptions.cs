namespace BonBon.Business.Configuration;

/// <summary>
/// Cấu hình gửi mail qua Gmail SMTP (dùng App Password, không phải mật khẩu Gmail).
/// Hướng dẫn tạo: Google Account → Bảo mật → Xác minh 2 bước → Mật khẩu ứng dụng.
/// Tất cả secret lấy từ biến môi trường / user-secrets, không commit vào git.
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string AppPassword { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "BonBon";

    /// <summary>OTP hết hạn sau N phút.</summary>
    public int OtpExpiryMinutes { get; set; } = 10;

    /// <summary>Nhập sai tối đa N lần thì hủy OTP.</summary>
    public int OtpMaxAttempts { get; set; } = 5;

    /// <summary>Chống spam: mỗi email chỉ được xin OTP mới sau N giây.</summary>
    public int ResendCooldownSeconds { get; set; } = 60;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(AppPassword)
        && !string.IsNullOrWhiteSpace(FromAddress);
}
