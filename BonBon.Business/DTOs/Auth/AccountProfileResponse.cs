namespace BonBon.Business.DTOs.Auth;

/// <summary>
/// Hồ sơ tài khoản hiển thị trên trang "Tài khoản của tôi".
/// Không chứa dữ liệu nhạy cảm (không có password hash, không có token).
/// </summary>
public sealed class AccountProfileResponse
{
    public Guid AccountId { get; init; }
    public string Email { get; init; } = default!;
    public string FullName { get; init; } = default!;
    public string? DisplayName { get; init; }
    public string? Phone { get; init; }
    public string Status { get; init; } = default!;
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// Tài khoản có mật khẩu local hay không. Tài khoản chỉ đăng nhập bằng Google
    /// có giá trị false — khi đó UI không nên cho đổi mật khẩu.
    /// </summary>
    public bool HasPassword { get; init; }
}
