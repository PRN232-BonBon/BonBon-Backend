namespace BonBon.Entities;

/// <summary>
/// Tài khoản người dùng. KHÔNG dùng làm DTO — chỉ tồn tại ở tầng DataAccess và Business.
/// </summary>
public class Account
{
    public Guid AccountId { get; set; }
    public string Email { get; set; } = default!;

    /// <summary>BCrypt hash. NULL khi tài khoản tạo qua Google OAuth.</summary>
    public string? PasswordHash { get; set; }

    public string FullName { get; set; } = default!;
    public string? DisplayName { get; set; }
    public string? Phone { get; set; }

    /// <summary>Active | Suspended | Inactive</summary>
    public string Status { get; set; } = "Active";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public ICollection<AccountRole> AccountRoles { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
