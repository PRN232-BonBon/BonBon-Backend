using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Auth;

/// <summary>Request body cho PUT /auth/profile — cập nhật hồ sơ tài khoản.</summary>
public sealed class UpdateProfileRequest
{
    /// <summary>Tên hiển thị. Để trống / null = không đổi.</summary>
    [MaxLength(150)]
    public string? DisplayName { get; init; }

    /// <summary>Số điện thoại. Để trống / null = không đổi.</summary>
    [Phone]
    [MaxLength(30)]
    public string? Phone { get; init; }
}
