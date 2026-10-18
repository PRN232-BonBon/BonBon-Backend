using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class BookingRepository(BonBonDbContext db) : IBookingRepository
{
    private readonly BonBonDbContext _db = db;

    public async Task<VehiclePricing?> GetVehiclePricingAsync(Guid vehicleId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT vehicle_id, hour_price, day_price, status
            FROM bonbon.vehicle
            WHERE vehicle_id = @vehicle_id
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("vehicle_id", vehicleId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new VehiclePricing(
                reader.GetGuid(0),
                reader.GetDecimal(1),
                reader.GetDecimal(2),
                reader.GetString(3));
        }, ct);
    }

    public async Task<bool> IsVehicleAvailableAsync(
        Guid vehicleId,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM bonbon.vehicle v
                WHERE v.vehicle_id = @vehicle_id
                  AND v.status = 'Available'
                  AND NOT EXISTS (
                      SELECT 1 FROM bonbon.availability_block ab
                      WHERE ab.vehicle_id = v.vehicle_id
                        AND tstzrange(ab.start_at, ab.end_at, '[)') &&
                            tstzrange(@start_at, @end_at, '[)')
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM bonbon.booking b
                      WHERE b.vehicle_id = v.vehicle_id
                        AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
                            'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
                        AND tstzrange(b.start_at, b.end_at, '[)') &&
                            tstzrange(@start_at, @end_at, '[)')
                  )
            )
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("vehicle_id", vehicleId);
            command.Parameters.AddWithValue("start_at", startAt.ToUniversalTime());
            command.Parameters.AddWithValue("end_at", endAt.ToUniversalTime());
            return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
        }, ct);
    }

    public async Task<CreatedBooking> CreateAsync(
        Guid customerAccountId,
        Guid vehicleId,
        string rentalType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT booking_id, vehicle_id, billable_units, unit_price_snapshot,
                   quoted_rental_amount, currency, status
            FROM bonbon.create_booking(
                @customer_account_id,
                @vehicle_id,
                @rental_type,
                @start_at,
                @end_at)
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("customer_account_id", customerAccountId);
            command.Parameters.AddWithValue("vehicle_id", vehicleId);
            command.Parameters.AddWithValue("rental_type", rentalType);
            command.Parameters.AddWithValue("start_at", startAt.ToUniversalTime());
            command.Parameters.AddWithValue("end_at", endAt.ToUniversalTime());

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Database did not return the created booking.");

            return new CreatedBooking(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                reader.GetString(6));
        }, ct);
    }

    public async Task<IReadOnlyList<CustomerBooking>> GetByCustomerAsync(
        Guid customerAccountId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT b.booking_id, v.brand, v.model, v.plate_number,
                   cover.image_url, b.rental_type, b.start_at, b.end_at,
                   b.quoted_rental_amount, b.currency, b.status, b.created_at
            FROM bonbon.booking b
            JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
            LEFT JOIN LATERAL (
                SELECT vi.image_url
                FROM bonbon.vehicle_image vi
                WHERE vi.vehicle_id = v.vehicle_id
                ORDER BY vi.is_cover DESC, vi.sort_order ASC
                LIMIT 1
            ) cover ON TRUE
            WHERE b.customer_account_id = @customer_account_id
            ORDER BY b.start_at DESC
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("customer_account_id", customerAccountId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<CustomerBooking>();
            while (await reader.ReadAsync(ct))
            {
                items.Add(new CustomerBooking(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetDecimal(8), reader.GetString(9), reader.GetString(10),
                    reader.GetFieldValue<DateTimeOffset>(11)));
            }
            return items;
        }, ct);
    }

    private async Task<T> WithConnectionAsync<T>(
        Func<NpgsqlConnection, Task<T>> action,
        CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);

        try
        {
            return await action(connection);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }
}
