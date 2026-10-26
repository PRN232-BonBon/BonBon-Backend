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
            SELECT vehicle_id, hour_price, day_price, status,
                   weekly_discount_enabled, weekly_discount_percent,
                   mileage_limit_enabled, daily_mileage_limit_km,
                   overage_fee_per_km, rental_terms
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
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetString(9));
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
                   weekly_discount_percent_snapshot, discount_amount_snapshot,
                   quoted_rental_amount, included_mileage_km_snapshot,
                   overage_fee_per_km_snapshot, rental_terms_snapshot, currency, status
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
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11));
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

    public async Task<BookingDetailRecord?> GetByIdAsync(Guid bookingId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT b.booking_id, b.status, b.rental_type, b.start_at, b.end_at, b.billable_units,
                   b.unit_price_snapshot, b.weekly_discount_percent_snapshot, b.discount_amount_snapshot,
                   b.quoted_rental_amount, b.included_mileage_km_snapshot, b.overage_fee_per_km_snapshot,
                   b.rental_terms_snapshot, b.currency, b.created_at,
                   v.vehicle_id, v.brand, v.model, v.plate_number, cover.image_url,
                   v.pickup_address, v.seats, v.transmission, v.fuel_type,
                   cust.account_id, cust.full_name, cust.email, cust.phone,
                   own.account_id, own.full_name, own.email, own.phone
            FROM bonbon.booking b
            JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
            JOIN bonbon.account cust ON cust.account_id = b.customer_account_id
            JOIN bonbon.account own ON own.account_id = v.owner_account_id
            LEFT JOIN LATERAL (
                SELECT vi.image_url
                FROM bonbon.vehicle_image vi
                WHERE vi.vehicle_id = v.vehicle_id
                ORDER BY vi.is_cover DESC, vi.sort_order ASC
                LIMIT 1
            ) cover ON TRUE
            WHERE b.booking_id = @booking_id
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new BookingDetailRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10),
                reader.IsDBNull(11) ? null : reader.GetDecimal(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.GetString(13),
                reader.GetFieldValue<DateTimeOffset>(14),
                reader.GetGuid(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetString(20),
                reader.GetInt16(21),
                reader.GetString(22),
                reader.GetString(23),
                reader.GetGuid(24),
                reader.GetString(25),
                reader.GetString(26),
                reader.IsDBNull(27) ? null : reader.GetString(27),
                reader.GetGuid(28),
                reader.GetString(29),
                reader.GetString(30),
                reader.IsDBNull(31) ? null : reader.GetString(31));
        }, ct);
    }

    public async Task<IReadOnlyList<OwnerBookingRecord>> GetByOwnerAsync(
        Guid ownerAccountId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT b.booking_id, v.vehicle_id, v.brand, v.model, v.plate_number,
                   cover.image_url, cust.full_name, cust.phone,
                   b.rental_type, b.start_at, b.end_at, b.quoted_rental_amount,
                   b.currency, b.status, b.created_at
            FROM bonbon.booking b
            JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
            JOIN bonbon.account cust ON cust.account_id = b.customer_account_id
            LEFT JOIN LATERAL (
                SELECT vi.image_url
                FROM bonbon.vehicle_image vi
                WHERE vi.vehicle_id = v.vehicle_id
                ORDER BY vi.is_cover DESC, vi.sort_order ASC
                LIMIT 1
            ) cover ON TRUE
            WHERE v.owner_account_id = @owner_account_id
            ORDER BY b.created_at DESC
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("owner_account_id", ownerAccountId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<OwnerBookingRecord>();
            while (await reader.ReadAsync(ct))
            {
                items.Add(new OwnerBookingRecord(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8), reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetFieldValue<DateTimeOffset>(10), reader.GetDecimal(11),
                    reader.GetString(12), reader.GetString(13),
                    reader.GetFieldValue<DateTimeOffset>(14)));
            }
            return items;
        }, ct);
    }

    public async Task<bool> CancelAsync(Guid bookingId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bonbon.booking
            SET status = 'Cancelled'
            WHERE booking_id = @booking_id
              AND status IN ('PendingApproval', 'AwaitingPayment')
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            var affected = await command.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }, ct);
    }

    public async Task<bool> ApproveAsync(Guid bookingId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bonbon.booking
            SET status = 'AwaitingPayment'
            WHERE booking_id = @booking_id
              AND status = 'PendingApproval'
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            var affected = await command.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }, ct);
    }

    public async Task<bool> RejectAsync(Guid bookingId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bonbon.booking
            SET status = 'Rejected'
            WHERE booking_id = @booking_id
              AND status = 'PendingApproval'
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            var affected = await command.ExecuteNonQueryAsync(ct);
            return affected > 0;
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
