using BonBon.Entities;

namespace BonBon.DataAccess.Repositories.Interfaces;

public interface IRefreshTokenRepository
{
    /// <summary>Lấy refresh token theo hash. Trả null nếu không tồn tại.</summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Lấy tất cả token còn active của account (để logout all devices).</summary>
    Task<IEnumerable<RefreshToken>> GetActiveByAccountIdAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Thêm refresh token mới. Chưa SaveChanges.</summary>
    Task AddAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>Revoke một token cụ thể (logout đơn). Chưa SaveChanges.</summary>
    Task RevokeAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>Revoke tất cả token của account (logout all devices). Chưa SaveChanges.</summary>
    Task RevokeAllByAccountIdAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Commit tất cả thay đổi pending.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
