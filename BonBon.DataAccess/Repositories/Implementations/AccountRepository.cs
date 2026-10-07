using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.EntityFrameworkCore;

namespace BonBon.DataAccess.Repositories.Implementations;

public class AccountRepository(BonBonDbContext db) : IAccountRepository
{
    private readonly BonBonDbContext _db = db;

    public async Task<Account?> GetByEmailAsync(string email, CancellationToken ct = default)
        => await _db.Accounts
            .Include(a => a.AccountRoles)
                .ThenInclude(ar => ar.Role)
            .FirstOrDefaultAsync(
                a => a.Email.ToLower() == email.Trim().ToLower(),
                ct);

    public async Task<Account?> GetByIdWithRolesAsync(Guid accountId, CancellationToken ct = default)
        => await _db.Accounts
            .Include(a => a.AccountRoles.Where(ar => ar.Status == "Active"))
                .ThenInclude(ar => ar.Role)
            .FirstOrDefaultAsync(a => a.AccountId == accountId, ct);

    public async Task<bool> ExistsByEmailAsync(string email, CancellationToken ct = default)
        => await _db.Accounts
            .AnyAsync(a => a.Email.ToLower() == email.Trim().ToLower(), ct);

    public async Task AddAsync(Account account, CancellationToken ct = default)
        => await _db.Accounts.AddAsync(account, ct);

    public async Task AddAccountRoleAsync(AccountRole accountRole, CancellationToken ct = default)
        => await _db.AccountRoles.AddAsync(accountRole, ct);

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
