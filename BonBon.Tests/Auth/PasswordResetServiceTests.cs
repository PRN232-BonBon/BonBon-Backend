using System.Security.Cryptography;
using System.Text;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Auth;

/// <summary>
/// Quên mật khẩu bằng OTP Gmail: chống liệt kê email, OTP sai/hết hạn,
/// đặt lại thành công và revoke mọi phiên.
/// </summary>
public class PasswordResetServiceTests
{
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static IOptions<SmtpOptions> Smtp() => Options.Create(new SmtpOptions
    {
        OtpExpiryMinutes = 10,
        OtpMaxAttempts = 5,
        ResendCooldownSeconds = 60,
    });

    private static string HashOtp(string otp)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(otp));
        return Convert.ToBase64String(bytes);
    }

    private static Account ActiveAccount() => new()
    {
        AccountId = AccountId,
        Email = "user@test.invalid",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("mat-khau-cu"),
        FullName = "Test User",
        Status = "Active",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static PasswordResetOtp StoredOtp(string otp, DateTimeOffset? expiresAt = null, int attempts = 0) => new()
    {
        Id = Guid.NewGuid(),
        Email = "user@test.invalid",
        OtpHash = HashOtp(otp),
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10),
        CreatedAt = DateTimeOffset.UtcNow,
        AttemptCount = attempts,
    };

    // Email lạ vẫn trả Success, không gửi mail, không tạo OTP (chống liệt kê).
    [Fact]
    public async Task Request_with_unknown_email_returns_success_without_sending()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByEmailAsync("la@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        var otps = new Mock<IPasswordResetOtpRepository>(MockBehavior.Strict);
        var mail = new Mock<IEmailSender>(MockBehavior.Strict);

        var service = new PasswordResetService(
            accounts.Object, otps.Object,
            new Mock<IRefreshTokenRepository>().Object,
            mail.Object, Smtp(), Mock.Of<ILogger<PasswordResetService>>());

        var result = await service.RequestAsync(new ForgotPasswordRequest { Email = "la@test.invalid" });

        Assert.True(result.IsSuccess);
        mail.VerifyNoOtherCalls();
        otps.VerifyNoOtherCalls();
    }

    // Email tồn tại: tạo OTP mới và gửi mail.
    [Fact]
    public async Task Request_with_known_email_sends_otp()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByEmailAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ActiveAccount());

        var otps = new Mock<IPasswordResetOtpRepository>();
        otps.Setup(x => x.GetLatestAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PasswordResetOtp?)null);

        var mail = new Mock<IEmailSender>();
        mail.Setup(x => x.SendPasswordResetOtpAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new PasswordResetService(
            accounts.Object, otps.Object,
            new Mock<IRefreshTokenRepository>().Object,
            mail.Object, Smtp(), Mock.Of<ILogger<PasswordResetService>>());

        var result = await service.RequestAsync(new ForgotPasswordRequest { Email = "User@Test.Invalid " });

        Assert.True(result.IsSuccess);
        otps.Verify(x => x.AddAsync(It.IsAny<PasswordResetOtp>(), It.IsAny<CancellationToken>()), Times.Once);
        mail.Verify(x => x.SendPasswordResetOtpAsync(
            "user@test.invalid", It.Is<string>(o => o.Length == 6), 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // OTP sai: tăng AttemptCount, báo số lần còn lại.
    [Fact]
    public async Task Reset_with_wrong_otp_increments_attempts()
    {
        var stored = StoredOtp("123456");

        var accounts = new Mock<IAccountRepository>();
        var otps = new Mock<IPasswordResetOtpRepository>();
        otps.Setup(x => x.GetLatestAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var service = new PasswordResetService(
            accounts.Object, otps.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IEmailSender>().Object, Smtp(), Mock.Of<ILogger<PasswordResetService>>());

        var result = await service.ResetAsync(new ResetPasswordRequest
        {
            Email = "user@test.invalid",
            Otp = "000000",
            NewPassword = "mat-khau-moi",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("OTP_INVALID", result.ErrorCode);
        Assert.Equal(1, stored.AttemptCount);
        otps.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // OTP hết hạn: đánh dấu đã tiêu thụ, yêu cầu gửi mã mới.
    [Fact]
    public async Task Reset_with_expired_otp_requires_new_code()
    {
        var stored = StoredOtp("123456", DateTimeOffset.UtcNow.AddMinutes(-1));

        var accounts = new Mock<IAccountRepository>();
        var otps = new Mock<IPasswordResetOtpRepository>();
        otps.Setup(x => x.GetLatestAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        var service = new PasswordResetService(
            accounts.Object, otps.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IEmailSender>().Object, Smtp(), Mock.Of<ILogger<PasswordResetService>>());

        var result = await service.ResetAsync(new ResetPasswordRequest
        {
            Email = "user@test.invalid",
            Otp = "123456",
            NewPassword = "mat-khau-moi",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("OTP_EXPIRED", result.ErrorCode);
        Assert.NotNull(stored.ConsumedAt);
    }

    // OTP đúng: đổi mật khẩu, vô hiệu OTP, revoke mọi refresh token.
    [Fact]
    public async Task Reset_with_correct_otp_changes_password_and_revokes_sessions()
    {
        var account = ActiveAccount();
        var stored = StoredOtp("123456");

        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByEmailAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var otps = new Mock<IPasswordResetOtpRepository>();
        otps.Setup(x => x.GetLatestAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        otps.Setup(x => x.ConsumeActiveForEmailAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .Callback(() => stored.ConsumedAt = DateTimeOffset.UtcNow)
            .Returns(Task.CompletedTask);

        var tokens = new Mock<IRefreshTokenRepository>();
        tokens.Setup(x => x.RevokeAllByAccountIdAsync(AccountId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new PasswordResetService(
            accounts.Object, otps.Object, tokens.Object,
            new Mock<IEmailSender>().Object, Smtp(), Mock.Of<ILogger<PasswordResetService>>());

        var result = await service.ResetAsync(new ResetPasswordRequest
        {
            Email = "user@test.invalid",
            Otp = "123456",
            NewPassword = "mat-khau-hoan-toan-moi",
        });

        Assert.True(result.IsSuccess);
        Assert.True(BCrypt.Net.BCrypt.Verify("mat-khau-hoan-toan-moi", account.PasswordHash));
        tokens.Verify(x => x.RevokeAllByAccountIdAsync(AccountId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
