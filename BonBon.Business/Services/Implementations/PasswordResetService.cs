using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Common;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BonBon.Business.Services.Implementations;

public class PasswordResetService(
    IAccountRepository accountRepo,
    IPasswordResetOtpRepository otpRepo,
    IRefreshTokenRepository refreshTokenRepo,
    IEmailSender emailSender,
    IOptions<SmtpOptions> smtpOptions,
    ILogger<PasswordResetService> logger) : IPasswordResetService
{
    private readonly IAccountRepository _accounts = accountRepo;
    private readonly IPasswordResetOtpRepository _otps = otpRepo;
    private readonly IRefreshTokenRepository _refreshTokens = refreshTokenRepo;
    private readonly IEmailSender _emailSender = emailSender;
    private readonly SmtpOptions _smtp = smtpOptions.Value;
    private readonly ILogger<PasswordResetService> _logger = logger;

    public async Task<ServiceResult> RequestAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);

        // Chống liệt kê tài khoản: email lạ vẫn trả Success như đã gửi.
        var account = await _accounts.GetByEmailAsync(email, ct);
        if (account is null || account.Status != "Active")
            return ServiceResult.Success();

        var now = DateTimeOffset.UtcNow;
        var latest = await _otps.GetLatestAsync(email, ct);
        if (latest is not null && latest.ConsumedAt is null && latest.ExpiresAt > now
            && (now - latest.CreatedAt).TotalSeconds < _smtp.ResendCooldownSeconds)
            return ServiceResult.Success(); // Trong cooldown: giả vờ đã gửi, không spam mail.

        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        await _otps.ConsumeActiveForEmailAsync(email, ct);
        await _otps.AddAsync(new PasswordResetOtp
        {
            Id = Guid.NewGuid(),
            Email = email,
            OtpHash = Hash(otp),
            ExpiresAt = now.AddMinutes(_smtp.OtpExpiryMinutes),
            CreatedAt = now,
        }, ct);
        await _otps.SaveChangesAsync(ct);

        try
        {
            await _emailSender.SendPasswordResetOtpAsync(account.Email, otp, _smtp.OtpExpiryMinutes, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gửi OTP đặt lại mật khẩu tới {Email} thất bại.", account.Email);
            return ServiceResult.Failure(
                "EMAIL_SEND_FAILED", "Không thể gửi email lúc này. Vui lòng thử lại sau.");
        }

        return ServiceResult.Success();
    }

    public async Task<ServiceResult> ResetAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);
        var now = DateTimeOffset.UtcNow;

        var latest = await _otps.GetLatestAsync(email, ct);
        if (latest is null || latest.ConsumedAt is not null)
            return ServiceResult.Failure(
                "OTP_INVALID", "Mã OTP không đúng hoặc đã hết hạn. Vui lòng gửi lại mã mới.");

        if (latest.ExpiresAt <= now)
        {
            latest.ConsumedAt = now;
            await _otps.SaveChangesAsync(ct);
            return ServiceResult.Failure(
                "OTP_EXPIRED", "Mã OTP đã hết hạn. Vui lòng gửi lại mã mới.");
        }

        if (latest.AttemptCount >= _smtp.OtpMaxAttempts)
            return ServiceResult.Failure(
                "OTP_LOCKED", "Bạn đã nhập sai quá nhiều lần. Vui lòng gửi lại mã mới.");

        if (!Verify(request.Otp, latest.OtpHash))
        {
            latest.AttemptCount++;
            await _otps.SaveChangesAsync(ct);
            var left = _smtp.OtpMaxAttempts - latest.AttemptCount;
            return ServiceResult.Failure(
                "OTP_INVALID",
                left > 0
                    ? $"Mã OTP chưa đúng. Bạn còn {left} lần thử."
                    : "Bạn đã nhập sai quá nhiều lần. Vui lòng gửi lại mã mới.");
        }

        var account = await _accounts.GetByEmailAsync(email, ct);
        if (account is null || account.Status != "Active")
            return ServiceResult.Failure(
                "OTP_INVALID", "Mã OTP không đúng hoặc đã hết hạn. Vui lòng gửi lại mã mới.");

        // Cho phép cả tài khoản Google-only (chưa có mật khẩu) đặt mật khẩu qua OTP.
        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        latest.ConsumedAt = now;
        await _otps.ConsumeActiveForEmailAsync(email, ct);
        await _otps.SaveChangesAsync(ct);

        // Mật khẩu mới = mọi phiên đăng nhập cũ không còn đáng tin.
        await _refreshTokens.RevokeAllByAccountIdAsync(account.AccountId, ct);
        await _refreshTokens.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }

    private static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static string Hash(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToBase64String(bytes);
    }

    private static bool Verify(string otp, string expectedHash)
    {
        var actual = Encoding.UTF8.GetBytes(Hash(otp.Trim()));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return actual.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
