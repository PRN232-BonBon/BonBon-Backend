namespace BonBon.Entities;

/// <summary>
/// Refresh token lưu DB để hỗ trợ revoke (logout / logout-all-devices).
/// </summary>
public class RefreshToken
{
    public Guid RefreshTokenId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Chuỗi token ngẫu nhiên (SHA-256 hash của giá trị gửi cho client).</summary>
    public string TokenHash { get; set; } = default!;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Thời điểm bị revoke. NULL = còn hiệu lực.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Thông tin thiết bị / user-agent (tuỳ chọn).</summary>
    public string? DeviceInfo { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsActive => !IsRevoked && !IsExpired;

    // Navigation
    public Account Account { get; set; } = default!;
}
