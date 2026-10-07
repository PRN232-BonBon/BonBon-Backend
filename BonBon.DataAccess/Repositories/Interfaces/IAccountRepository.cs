using BonBon.Entities;

namespace BonBon.DataAccess.Repositories.Interfaces;

public interface IAccountRepository
{
    /// <summary>Tìm account theo email (case-insensitive). Trả null nếu không tồn tại.</summary>
    Task<Account?> GetByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Tìm account theo ID, kèm danh sách roles đang Active.</summary>
    Task<Account?> GetByIdWithRolesAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Kiểm tra email đã tồn tại chưa.</summary>
    Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>Thêm account mới. Chưa SaveChanges.</summary>
    Task AddAsync(Account account, CancellationToken ct = default);

    /// <summary>Thêm AccountRole mới cho account. Chưa SaveChanges.</summary>
    Task AddAccountRoleAsync(AccountRole accountRole, CancellationToken ct = default);

    /// <summary>Commit tất cả thay đổi pending.</summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
