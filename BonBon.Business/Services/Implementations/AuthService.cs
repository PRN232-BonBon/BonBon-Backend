using BonBon.Business.Common;
using BonBon.Business.DTOs.Auth;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Google.Apis.Auth;
using Microsoft.Extensions.Configuration;

namespace BonBon.Business.Services.Implementations;

public class AuthService(
    IAccountRepository      accountRepo,
    IRefreshTokenRepository refreshTokenRepo,
    IJwtService             jwtService,
    IConfiguration          config) : IAuthService
{
    private readonly IAccountRepository      _accountRepo      = accountRepo;
    private readonly IRefreshTokenRepository _refreshTokenRepo = refreshTokenRepo;
    private readonly IJwtService             _jwtService       = jwtService;
    private readonly IConfiguration          _config           = config;

    // -------------------------------------------------------------------------
    // Register
    // -------------------------------------------------------------------------
    public async Task<ServiceResult<AuthResponse>> RegisterAsync(
        RegisterRequest request, CancellationToken ct = default)
    {
        if (await _accountRepo.ExistsByEmailAsync(request.Email, ct))
            return ServiceResult<AuthResponse>.Failure(
                "EMAIL_TAKEN", "Email đã được sử dụng.");

        var account = new Account
        {
            AccountId    = Guid.NewGuid(),
            Email        = request.Email.Trim().ToLower(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FullName     = request.FullName.Trim(),
            DisplayName  = request.DisplayName?.Trim(),
            Phone        = request.Phone?.Trim(),
            Status       = "Active",
            CreatedAt    = DateTimeOffset.UtcNow,
        };

        // Tự đăng ký → luôn tạo Customer role (Active ngay)
        var customerRole = new AccountRole
        {
            AccountId  = account.AccountId,
            RoleId     = 2, // Customer
            Status     = "Active",
            AssignedAt = DateTimeOffset.UtcNow,
        };

        await _accountRepo.AddAsync(account, ct);
        await _accountRepo.AddAccountRoleAsync(customerRole, ct);
        await _accountRepo.SaveChangesAsync(ct);

        return ServiceResult<AuthResponse>.Success(
            await BuildAuthResponseAsync(account, ["Customer"], request.DeviceInfo, ct));
    }

    // -------------------------------------------------------------------------
    // Login
    // -------------------------------------------------------------------------
    public async Task<ServiceResult<AuthResponse>> LoginAsync(
        LoginRequest request, CancellationToken ct = default)
    {
        var account = await _accountRepo.GetByEmailAsync(request.Email, ct);

        if (account is null || account.PasswordHash is null)
            return ServiceResult<AuthResponse>.Failure(
                "INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");

        if (!BCrypt.Net.BCrypt.Verify(request.Password, account.PasswordHash))
            return ServiceResult<AuthResponse>.Failure(
                "INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");

        if (account.Status != "Active")
            return ServiceResult<AuthResponse>.Failure(
                "ACCOUNT_INACTIVE", "Tài khoản đã bị khoá hoặc vô hiệu hoá.");

        var activeRoles = account.AccountRoles
            .Where(ar => ar.Status == "Active")
            .Select(ar => ar.Role.Code)
            .ToList();

        return ServiceResult<AuthResponse>.Success(
            await BuildAuthResponseAsync(account, activeRoles, request.DeviceInfo, ct));
    }

    // -------------------------------------------------------------------------
    // Google Login
    // -------------------------------------------------------------------------
    public async Task<ServiceResult<AuthResponse>> GoogleLoginAsync(
        GoogleLoginRequest request, CancellationToken ct = default)
    {
        GoogleJsonWebSignature.Payload payload;
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = [_config["Google:ClientId"]
                    ?? throw new InvalidOperationException("Google:ClientId is not configured.")],
            };
            payload = await GoogleJsonWebSignature.ValidateAsync(request.IdToken, settings);
        }
        catch (InvalidJwtException)
        {
            return ServiceResult<AuthResponse>.Failure(
                "INVALID_GOOGLE_TOKEN", "Google ID Token không hợp lệ hoặc đã hết hạn.");
        }

        var email = payload.Email.Trim().ToLower();
        var account = await _accountRepo.GetByEmailAsync(email, ct);

        List<string> activeRoles;

        if (account is null)
        {
            // Tạo tài khoản mới qua Google — không có password
            account = new Account
            {
                AccountId   = Guid.NewGuid(),
                Email       = email,
                PasswordHash = "Pass_xai_tam_de_demo_syabau",
                FullName    = payload.Name ?? email,
                DisplayName = payload.GivenName,
                Status      = "Active",
                CreatedAt   = DateTimeOffset.UtcNow,
            };

            var customerRole = new AccountRole
            {
                AccountId  = account.AccountId,
                RoleId     = 2, // Customer
                Status     = "Active",
                AssignedAt = DateTimeOffset.UtcNow,
            };

            await _accountRepo.AddAsync(account, ct);
            await _accountRepo.AddAccountRoleAsync(customerRole, ct);
            await _accountRepo.SaveChangesAsync(ct);

            activeRoles = ["Customer"];
        }
        else
        {
            if (account.Status != "Active")
                return ServiceResult<AuthResponse>.Failure(
                    "ACCOUNT_INACTIVE", "Tài khoản đã bị khoá hoặc vô hiệu hoá.");

            activeRoles = account.AccountRoles
                .Where(ar => ar.Status == "Active")
                .Select(ar => ar.Role.Code)
                .ToList();
        }

        return ServiceResult<AuthResponse>.Success(
            await BuildAuthResponseAsync(account, activeRoles, request.DeviceInfo, ct));
    }

    // -------------------------------------------------------------------------
    // Refresh
    // -------------------------------------------------------------------------
    public async Task<ServiceResult<AuthResponse>> RefreshAsync(
        RefreshTokenRequest request, CancellationToken ct = default)
    {
        var tokenHash     = HashToken(request.RefreshToken);
        var storedToken   = await _refreshTokenRepo.GetByHashAsync(tokenHash, ct);

        if (storedToken is null || !storedToken.IsActive)
            return ServiceResult<AuthResponse>.Failure(
                "INVALID_REFRESH_TOKEN", "Refresh token không hợp lệ hoặc đã hết hạn.");

        if (storedToken.Account.Status != "Active")
            return ServiceResult<AuthResponse>.Failure(
                "ACCOUNT_INACTIVE", "Tài khoản đã bị khoá hoặc vô hiệu hoá.");

        // Rotate: revoke token cũ, cấp token mới
        await _refreshTokenRepo.RevokeAsync(storedToken, ct);

        var account = await _accountRepo.GetByIdWithRolesAsync(storedToken.AccountId, ct)
            ?? throw new InvalidOperationException("Account not found during token refresh.");

        var activeRoles = account.AccountRoles
            .Select(ar => ar.Role.Code)
            .ToList();

        var response = await BuildAuthResponseAsync(
            account, activeRoles, storedToken.DeviceInfo, ct);

        await _refreshTokenRepo.SaveChangesAsync(ct);

        return ServiceResult<AuthResponse>.Success(response);
    }

    // -------------------------------------------------------------------------
    // Logout
    // -------------------------------------------------------------------------
    public async Task<ServiceResult> LogoutAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var tokenHash   = HashToken(rawRefreshToken);
        var storedToken = await _refreshTokenRepo.GetByHashAsync(tokenHash, ct);

        if (storedToken is null || !storedToken.IsActive)
            return ServiceResult.Failure("INVALID_REFRESH_TOKEN", "Refresh token không hợp lệ.");

        await _refreshTokenRepo.RevokeAsync(storedToken, ct);
        await _refreshTokenRepo.SaveChangesAsync(ct);

        return ServiceResult.Success();
    }

    public async Task<ServiceResult> LogoutAllDevicesAsync(Guid accountId, CancellationToken ct = default)
    {
        await _refreshTokenRepo.RevokeAllByAccountIdAsync(accountId, ct);
        await _refreshTokenRepo.SaveChangesAsync(ct);
        return ServiceResult.Success();
    }

    public async Task<ServiceResult> ChangePasswordAsync(
        Guid accountId,
        ChangePasswordRequest request,
        CancellationToken ct = default)
    {
        var account = await _accountRepo.GetByIdWithRolesAsync(accountId, ct);
        if (account is null)
            return ServiceResult.Failure("ACCOUNT_NOT_FOUND", "Không tìm thấy tài khoản.");
        if (string.IsNullOrWhiteSpace(account.PasswordHash) ||
            !BCrypt.Net.BCrypt.Verify(request.CurrentPassword, account.PasswordHash))
            return ServiceResult.Failure("INVALID_CURRENT_PASSWORD", "Mật khẩu hiện tại không đúng.");
        if (request.CurrentPassword == request.NewPassword)
            return ServiceResult.Failure("PASSWORD_UNCHANGED", "Mật khẩu mới phải khác mật khẩu hiện tại.");

        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
        await _accountRepo.SaveChangesAsync(ct);
        return ServiceResult.Success();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<AuthResponse> BuildAuthResponseAsync(
        Account        account,
        IList<string>  roles,
        string?        deviceInfo,
        CancellationToken ct)
    {
        var (accessToken, expiresAt)    = _jwtService.GenerateAccessToken(account, roles);
        var (rawRefresh, refreshHash)   = _jwtService.GenerateRefreshToken();

        var refreshTokenDays = int.Parse(_config["Jwt:RefreshTokenDays"] ?? "30");

        var refreshToken = new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            AccountId      = account.AccountId,
            TokenHash      = refreshHash,
            ExpiresAt      = DateTimeOffset.UtcNow.AddDays(refreshTokenDays),
            CreatedAt      = DateTimeOffset.UtcNow,
            DeviceInfo     = deviceInfo,
        };

        await _refreshTokenRepo.AddAsync(refreshToken, ct);
        await _refreshTokenRepo.SaveChangesAsync(ct);

        return new AuthResponse
        {
            AccessToken          = accessToken,
            RefreshToken         = rawRefresh,
            AccessTokenExpiresAt = expiresAt,
            Account = new AccountInfo
            {
                AccountId   = account.AccountId,
                Email       = account.Email,
                FullName    = account.FullName,
                DisplayName = account.DisplayName,
                Roles       = roles.AsReadOnly(),
            },
        };
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }
}
