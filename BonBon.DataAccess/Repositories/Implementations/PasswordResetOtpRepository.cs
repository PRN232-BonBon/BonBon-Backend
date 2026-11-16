using BonBon.DataAccess.Repositories.Interfaces;
using BonBon.Entities;
using Microsoft.EntityFrameworkCore;

namespace BonBon.DataAccess.Repositories.Implementations;

public class PasswordResetOtpRepository(BonBonDbContext db) : IPasswordResetOtpRepository
{
    private readonly BonBonDbContext _db = db;

    public async Task<PasswordResetOtp?> GetLatestAsync(string email, CancellationToken ct = default)
        => await _db.PasswordResetOtps
            .Where(o => o.Email == email)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task ConsumeActiveForEmailAsync(string email, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await _db.PasswordResetOtps
            .Where(o => o.Email == email && o.ConsumedAt == null && o.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var otp in active)
            otp.ConsumedAt = now;
    }

    public async Task AddAsync(PasswordResetOtp otp, CancellationToken ct = default)
        => await _db.PasswordResetOtps.AddAsync(otp, ct);

    public Task<int> SaveChangesAsync(CancellationToken ct = default)
        => _db.SaveChangesAsync(ct);
}
