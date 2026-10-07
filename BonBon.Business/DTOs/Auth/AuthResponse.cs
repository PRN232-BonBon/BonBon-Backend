namespace BonBon.Business.DTOs.Auth;

/// <summary>
/// Response trả về sau login / register / refresh thành công.
/// KHÔNG chứa entity — chỉ data cần thiết cho client.
/// </summary>
public sealed class AuthResponse
{
    public string AccessToken { get; init; } = default!;
    public string RefreshToken { get; init; } = default!;

    /// <summary>Thời gian hết hạn của access token (Unix timestamp seconds).</summary>
    public long AccessTokenExpiresAt { get; init; }

    public AccountInfo Account { get; init; } = default!;
}

public sealed class AccountInfo
{
    public Guid AccountId { get; init; }
    public string Email { get; init; } = default!;
    public string FullName { get; init; } = default!;
    public string? DisplayName { get; init; }

    /// <summary>Danh sách code của các role đang Active: "Customer", "VehicleOwner", "Admin".</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];
}
