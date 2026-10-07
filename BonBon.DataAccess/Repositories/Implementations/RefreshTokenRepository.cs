using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.EntityFrameworkCore;

namespace BonBon.DataAccess.Repositories.Implementations;

public class RefreshTokenRepository(BonBonDbContext db) : IRefreshTokenRepository
{
    private readonly BonBonDbContext _db = db;

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
        => await _db.RefreshTokens
            .Include(t => t.Account)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task<IEnumerable<RefreshToken>> GetActiveByAccountIdAsync(
        Guid accountId, CancellationToken ct = default)
        => await _db.RefreshTokens
            .Where(t => t.AccountId == accountId
                     && t.RevokedAt == null
                     && t.ExpiresAt > DateTimeOffset.UtcNow)
            .ToListAsync(ct);

    public async Task AddAsync(RefreshToken token, CancellationToken ct = default)
        => await _db.RefreshTokens.AddAsync(token, ct);

    public Task RevokeAsync(RefreshToken token, CancellationToken ct = default)
    {
        token.RevokedAt = DateTimeOffset.UtcNow;
        _db.RefreshTokens.Update(token);
        return Task.CompletedTask;
    }

    public async Task RevokeAllByAccountIdAsync(Guid accountId, CancellationToken ct = default)
    {
        var activeTokens = await _db.RefreshTokens
            .Where(t => t.AccountId == accountId
                     && t.RevokedAt == null
                     && t.ExpiresAt > DateTimeOffset.UtcNow)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var token in activeTokens)
            token.RevokedAt = now;
    }

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
