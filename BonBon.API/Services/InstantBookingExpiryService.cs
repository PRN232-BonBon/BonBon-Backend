using BonBon.DataAccess;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.API.Services;

// Vehicle -> booking is the same lock order used by checkout and payment IPN.
public sealed class InstantBookingExpiryService(IServiceScopeFactory scopes, ILogger<InstantBookingExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await ExpireBatchAsync(scope.ServiceProvider.GetRequiredService<BonBonDbContext>(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Could not expire instant booking holds"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    public static async Task ExpireBatchAsync(BonBonDbContext db, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(ct);
        var candidates = new List<(Guid Booking, Guid Vehicle)>();
        await using (var select = new NpgsqlCommand("""
            SELECT booking_id, vehicle_id FROM bonbon.booking
            WHERE status = 'AwaitingPayment' AND hold_expires_at IS NOT NULL AND hold_expires_at <= clock_timestamp()
            ORDER BY hold_expires_at LIMIT 100
            """, connection))
        await using (var reader = await select.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) candidates.Add((reader.GetGuid(0), reader.GetGuid(1)));

        foreach (var (booking, vehicle) in candidates)
        {
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var guard = new NpgsqlCommand("SELECT 1 FROM bonbon.vehicle WHERE vehicle_id=@vehicle FOR UPDATE", connection, tx))
            {
                guard.Parameters.AddWithValue("vehicle", vehicle);
                await guard.ExecuteNonQueryAsync(ct);
            }
            await using (var expire = new NpgsqlCommand("""
                UPDATE bonbon.booking SET status='Cancelled'
                WHERE booking_id=@booking AND vehicle_id=@vehicle AND status='AwaitingPayment'
                  AND hold_expires_at IS NOT NULL AND hold_expires_at <= clock_timestamp()
                """, connection, tx))
            {
                expire.Parameters.AddWithValue("booking", booking);
                expire.Parameters.AddWithValue("vehicle", vehicle);
                await expire.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
    }
}
