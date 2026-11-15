using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class SePayPaymentRepository(BonBonDbContext db) : ISePayPaymentRepository
{
    public IPaymentRepository Payments { get; } = new PaymentRepository(db, "SEPAY_SANDBOX");
    public async Task RecordRejectedAsync(string reason, string fingerprint, DateTimeOffset now, CancellationToken ct)
    {
        // Only fixed reason codes and a hash; no payload, IP, key or personal/card data.
        var c = (NpgsqlConnection)db.Database.GetDbConnection(); var close = c.State != System.Data.ConnectionState.Open;
        if (close) await c.OpenAsync(ct);
        try {
            await using var cmd = new NpgsqlCommand("INSERT INTO bonbon.sepay_rejected_notification(fingerprint,reason,received_at) VALUES(@hash,@reason,@now) ON CONFLICT DO NOTHING", c);
            cmd.Parameters.AddWithValue("hash", fingerprint); cmd.Parameters.AddWithValue("reason", reason); cmd.Parameters.AddWithValue("now", now);
            await cmd.ExecuteNonQueryAsync(ct);
        } finally { if (close) await c.CloseAsync(); }
    }
    public async Task<bool> IsActiveAdminAsync(Guid account, CancellationToken ct)
    {
        var c = (NpgsqlConnection)db.Database.GetDbConnection(); var close = c.State != System.Data.ConnectionState.Open;
        if (close) await c.OpenAsync(ct);
        try {
            await using var cmd = new NpgsqlCommand("SELECT bonbon.has_active_role(@id,1::smallint)", c);
            cmd.Parameters.AddWithValue("id", account); return await cmd.ExecuteScalarAsync(ct) is true;
        } finally { if (close) await c.CloseAsync(); }
    }
}
