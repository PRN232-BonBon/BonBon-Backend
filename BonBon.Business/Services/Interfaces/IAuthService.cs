using BonBon.Business.Common;
using BonBon.Business.DTOs.Auth;

namespace BonBon.Business.Services.Interfaces;

public interface IAuthService
{
    /// <summary>Đăng ký tài khoản mới với role Customer.</summary>
    Task<ServiceResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);

    /// <summary>Đăng nhập bằng email + password.</summary>
    Task<ServiceResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);

    /// <summary>Đăng nhập / đăng ký qua Google ID Token.</summary>
    Task<ServiceResult<AuthResponse>> GoogleLoginAsync(GoogleLoginRequest request, CancellationToken ct = default);

    /// <summary>Làm mới access token bằng refresh token hợp lệ.</summary>
    Task<ServiceResult<AuthResponse>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default);

    /// <summary>Đăng xuất — revoke refresh token hiện tại.</summary>
    Task<ServiceResult> LogoutAsync(string rawRefreshToken, CancellationToken ct = default);

    /// <summary>Đăng xuất toàn bộ thiết bị — revoke tất cả refresh token của account.</summary>
    Task<ServiceResult> LogoutAllDevicesAsync(Guid accountId, CancellationToken ct = default);

    Task<ServiceResult> ChangePasswordAsync(Guid accountId, ChangePasswordRequest request, CancellationToken ct = default);
}
