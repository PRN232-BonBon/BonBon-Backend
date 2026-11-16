namespace BonBon.Entities;

/// <summary>
/// OTP đặt lại mật khẩu gửi qua email. Chỉ lưu SHA-256 hash, không lưu mã gốc.
/// </summary>
public class PasswordResetOtp
{
    public Guid Id { get; set; }

    /// <summary>Email đã chuẩn hóa (trim + lowercase).</summary>
    public string Email { get; set; } = default!;

    /// <summary>SHA-256 (Base64) của mã OTP 6 chữ số.</summary>
    public string OtpHash { get; set; } = default!;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Thời điểm OTP bị vô hiệu (đã dùng / hết hạn / bị thay thế). NULL = còn hiệu lực.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public int AttemptCount { get; set; }

    public bool IsActive => ConsumedAt is null && DateTimeOffset.UtcNow < ExpiresAt;
}
