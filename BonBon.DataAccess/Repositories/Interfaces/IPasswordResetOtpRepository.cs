using BonBon.Entities;

namespace BonBon.DataAccess.Repositories.Interfaces;

public interface IPasswordResetOtpRepository
{
    /// <summary>Lấy OTP mới nhất của email (đang track, để cập nhật AttemptCount/ConsumedAt).</summary>
    Task<PasswordResetOtp?> GetLatestAsync(string email, CancellationToken ct = default);

    /// <summary>Đánh dấu tất cả OTP còn hiệu lực của email là đã vô hiệu. Chưa SaveChanges.</summary>
    Task ConsumeActiveForEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Thêm OTP mới. Chưa SaveChanges.</summary>
    Task AddAsync(PasswordResetOtp otp, CancellationToken ct = default);

    /// <summary>Commit tất cả thay đổi pending.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
