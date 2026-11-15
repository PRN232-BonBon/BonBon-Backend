using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.Extensions.Configuration;
using Moq;

namespace BonBon.Tests.Auth;

/// <summary>
/// Kiểm tra các lỗi Phase 1 về auth:
///   AUTH-02 — tài khoản Google không được lưu PasswordHash giả.
///   SESSION-01 — refresh token phải tiêu thụ nguyên tử.
///   PROFILE-01/02 — hồ sơ đọc/ghi thật, phân biệt tài khoản có mật khẩu.
/// </summary>
public class AuthServiceTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "unit-test-signing-key-that-is-long-enough-32",
            ["Jwt:Issuer"] = "BonBon.API",
            ["Jwt:Audience"] = "BonBon.Client",
            ["Jwt:ExpiryMinutes"] = "60",
            ["Jwt:RefreshTokenDays"] = "30",
        })
        .Build();

    private static Account AccountWith(string? passwordHash, string status = "Active") => new()
    {
        AccountId    = AccountId,
        Email        = "user@test.invalid",
        PasswordHash = passwordHash,
        FullName     = "Test User",
        Status       = status,
        CreatedAt    = DateTimeOffset.UtcNow,
        AccountRoles =
        [
            new AccountRole
            {
                AccountId = AccountId,
                RoleId    = 2,
                Status    = "Active",
                Role      = new Role { RoleId = 2, Code = "Customer" },
            },
        ],
    };

    // -------------------------------------------------------------------------
    // AUTH-02: tài khoản Google-only không có mật khẩu local
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Login_with_google_only_account_returns_business_error_not_exception()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByEmailAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: null));

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.LoginAsync(new LoginRequest
        {
            Email = "user@test.invalid",
            Password = "batky",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_CREDENTIALS", result.ErrorCode);
    }

    [Fact]
    public async Task Login_with_corrupt_password_hash_returns_business_error_not_exception()
    {
        // Placeholder cũ từng ghi cho tài khoản Google — không phải BCrypt hash.
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByEmailAsync("user@test.invalid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: "Pass_xai_tam_de_demo_syabau"));

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.LoginAsync(new LoginRequest
        {
            Email = "user@test.invalid",
            Password = "batky",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_CREDENTIALS", result.ErrorCode);
    }

    [Fact]
    public async Task Change_password_for_google_only_account_returns_business_error()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: null));

        var tokens = new Mock<IRefreshTokenRepository>(MockBehavior.Strict);

        var service = new AuthService(
            accounts.Object,
            tokens.Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.ChangePasswordAsync(AccountId, new ChangePasswordRequest
        {
            CurrentPassword = "cu",
            NewPassword = "moi_mat_khau",
        });

        Assert.False(result.IsSuccess);
        Assert.Equal("PASSWORD_NOT_SET", result.ErrorCode);
        tokens.VerifyNoOtherCalls();
    }

    // -------------------------------------------------------------------------
    // SESSION-01: refresh token phải tiêu thụ nguyên tử
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Refresh_fails_with_business_error_when_token_lost_the_race()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau")));

        var storedToken = new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            AccountId      = AccountId,
            TokenHash      = "hash",
            ExpiresAt      = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt      = DateTimeOffset.UtcNow,
            DeviceInfo     = "test",
            Account        = AccountWith(passwordHash: null),
        };

        var tokens = new Mock<IRefreshTokenRepository>();
        tokens.Setup(x => x.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedToken);
        // Request thua trong race: rotate trả false, KHÔNG tạo token mới.
        tokens.Setup(x => x.RotateAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var jwt = new Mock<IJwtService>();
        jwt.Setup(x => x.GenerateAccessToken(It.IsAny<Account>(), It.IsAny<IEnumerable<string>>()))
            .Returns(("access", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        jwt.Setup(x => x.GenerateRefreshToken()).Returns(("raw", "newhash"));

        var service = new AuthService(accounts.Object, tokens.Object, jwt.Object, Configuration());

        var result = await service.RefreshAsync(new RefreshTokenRequest { RefreshToken = "raw-token" });

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_REFRESH_TOKEN", result.ErrorCode);
        tokens.Verify(
            x => x.RotateAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
        // Không được tạo refresh token mới bằng đường khác.
        tokens.Verify(x => x.AddAsync(It.IsAny<RefreshToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_succeeds_when_rotation_wins()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau")));

        var storedToken = new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            AccountId      = AccountId,
            TokenHash      = "hash",
            ExpiresAt      = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt      = DateTimeOffset.UtcNow,
            DeviceInfo     = "test",
            Account        = AccountWith(passwordHash: null),
        };

        var tokens = new Mock<IRefreshTokenRepository>();
        tokens.Setup(x => x.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(storedToken);
        tokens.Setup(x => x.RotateAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var jwt = new Mock<IJwtService>();
        jwt.Setup(x => x.GenerateAccessToken(It.IsAny<Account>(), It.IsAny<IEnumerable<string>>()))
            .Returns(("access", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        jwt.Setup(x => x.GenerateRefreshToken()).Returns(("raw", "newhash"));

        var service = new AuthService(accounts.Object, tokens.Object, jwt.Object, Configuration());

        var result = await service.RefreshAsync(new RefreshTokenRequest { RefreshToken = "raw-token" });

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.Equal("raw", result.Data!.RefreshToken);
    }

    // -------------------------------------------------------------------------
    // PROFILE: hồ sơ thật + phân biệt tài khoản có mật khẩu
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Profile_reports_real_created_at_and_password_presence()
    {
        var createdAt = new DateTimeOffset(2024, 3, 15, 8, 30, 0, TimeSpan.Zero);
        var account = AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau"));
        account.CreatedAt = createdAt;

        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.GetProfileAsync(AccountId);

        Assert.True(result.IsSuccess);
        Assert.Equal(createdAt, result.Data!.CreatedAt);
        Assert.True(result.Data.HasPassword);
        Assert.Contains("Customer", result.Data.Roles);
    }

    [Fact]
    public async Task Profile_reports_no_password_for_google_only_account()
    {
        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountWith(passwordHash: null));

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.GetProfileAsync(AccountId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.HasPassword);
    }

    [Fact]
    public async Task Update_profile_persists_display_name_and_phone()
    {
        var account = AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau"));

        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        accounts.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.UpdateProfileAsync(AccountId, new UpdateProfileRequest
        {
            DisplayName = "Nguyễn Văn A",
            Phone = "0901234567",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("Nguyễn Văn A", result.Data!.DisplayName);
        Assert.Equal("0901234567", result.Data.Phone);
        Assert.Equal("Nguyễn Văn A", account.DisplayName);
        Assert.Equal("0901234567", account.Phone);
        accounts.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_profile_keeps_existing_values_when_fields_are_null()
    {
        var account = AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau"));
        account.DisplayName = "Giữ nguyên";
        account.Phone = "0900000000";

        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        accounts.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = new AuthService(
            accounts.Object,
            new Mock<IRefreshTokenRepository>().Object,
            new Mock<IJwtService>().Object,
            Configuration());

        var result = await service.UpdateProfileAsync(AccountId, new UpdateProfileRequest
        {
            DisplayName = null,
            Phone = null,
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("Giữ nguyên", result.Data!.DisplayName);
        Assert.Equal("0900000000", result.Data.Phone);
    }

    // -------------------------------------------------------------------------
    // Chính sách phiên: đổi mật khẩu phải revoke refresh token của mọi thiết bị
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Change_password_revokes_all_refresh_tokens()
    {
        var account = AccountWith(passwordHash: BCrypt.Net.BCrypt.HashPassword("matkhau"));

        var accounts = new Mock<IAccountRepository>();
        accounts.Setup(x => x.GetByIdWithRolesAsync(AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(account);
        accounts.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var tokens = new Mock<IRefreshTokenRepository>();
        tokens.Setup(x => x.RevokeAllByAccountIdAsync(AccountId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        tokens.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var service = new AuthService(accounts.Object, tokens.Object, new Mock<IJwtService>().Object, Configuration());

        var result = await service.ChangePasswordAsync(AccountId, new ChangePasswordRequest
        {
            CurrentPassword = "matkhau",
            NewPassword = "mat_khau_moi",
        });

        Assert.True(result.IsSuccess);
        tokens.Verify(x => x.RevokeAllByAccountIdAsync(AccountId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
