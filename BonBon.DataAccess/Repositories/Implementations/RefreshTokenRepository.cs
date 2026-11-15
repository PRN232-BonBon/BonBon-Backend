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

    /// <summary>
    /// Rotate refresh token trong một transaction duy nhất: revoke token cũ
    /// (compare-and-swap) và thêm token mới.
    /// Trả false nếu token cũ không còn active — tức là request này thua trong
    /// race với một request refresh khác. Khi đó token mới KHÔNG được tạo.
    /// </summary>
    public async Task<bool> RotateAsync(
        string currentTokenHash,
        Guid accountId,
        string? deviceInfo,
        string newTokenHash,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        // Compare-and-swap: điều kiện nằm trong chính câu UPDATE nên chỉ một
        // request đồng thời nhận được affected = 1. Request còn lại nhận 0 và
        // phải từ chối token đã bị tiêu thụ.
        var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE bonbon.refresh_token
             SET revoked_at = {DateTimeOffset.UtcNow}
             WHERE token_hash = {currentTokenHash} AND revoked_at IS NULL
             """, ct);

        if (affected == 0)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await _db.RefreshTokens.AddAsync(new RefreshToken
        {
            RefreshTokenId = Guid.NewGuid(),
            AccountId      = accountId,
            TokenHash      = newTokenHash,
            ExpiresAt      = expiresAt,
            CreatedAt      = DateTimeOffset.UtcNow,
            DeviceInfo     = deviceInfo,
        }, ct);

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
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
