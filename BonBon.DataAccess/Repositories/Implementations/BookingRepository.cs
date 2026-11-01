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
            SELECT v.vehicle_id, v.owner_account_id,
                   bonbon.has_active_role(v.owner_account_id, 3::smallint) AS is_owner_active,
                   v.hour_price, v.day_price, v.status,
                   v.weekly_discount_enabled, v.weekly_discount_percent,
                   v.mileage_limit_enabled, v.daily_mileage_limit_km,
                   v.overage_fee_per_km, v.rental_terms
            FROM bonbon.vehicle v
            WHERE v.vehicle_id = @vehicle_id
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("vehicle_id", vehicleId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new VehiclePricing(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetBoolean(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.GetBoolean(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.IsDBNull(11) ? null : reader.GetString(11));
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

    public async Task<bool> HasDuplicatePendingBookingAsync(
        Guid customerAccountId,
        Guid vehicleId,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM bonbon.booking
                WHERE customer_account_id = @customer_account_id
                  AND vehicle_id = @vehicle_id
                  AND status IN ('PendingApproval', 'AwaitingPayment')
                  AND tstzrange(start_at, end_at, '[)') && tstzrange(@start_at, @end_at, '[)')
            )
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("customer_account_id", customerAccountId);
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
                SELECT COALESCE(vi.image_url, '/api/vehicles/images/' || vi.vehicle_image_id::text) AS image_url
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
                SELECT COALESCE(vi.image_url, '/api/vehicles/images/' || vi.vehicle_image_id::text) AS image_url
                FROM bonbon.vehicle_image vi
                WHERE vi.vehicle_id = v.vehicle_id
                ORDER BY vi.is_cover DESC, vi.sort_order ASC
                LIMIT 1
            ) cover ON TRUE
            WHERE b.booking_id = @booking_id
            """;

        return await WithConnectionAsync(async connection =>
        {
            BookingDetailRecord? detail = null;
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("booking_id", bookingId);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                if (await reader.ReadAsync(ct))
                {
                    detail = new BookingDetailRecord(
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
                        reader.IsDBNull(31) ? null : reader.GetString(31),
                        [], []);
                }
            }

            if (detail is null) return null;

            // Load inspections
            var inspections = await LoadInspectionsAsync(connection, bookingId, ct);
            // Load charges
            var charges = await LoadChargesAsync(connection, bookingId, ct);

            return detail with { Inspections = inspections, AdditionalCharges = charges };
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
                SELECT COALESCE(vi.image_url, '/api/vehicles/images/' || vi.vehicle_image_id::text) AS image_url
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

    public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bonbon.booking
            SET status = 'Confirmed'
            WHERE booking_id = @booking_id
              AND status = 'AwaitingPayment'
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            var affected = await command.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }, ct);
    }

    public async Task<InspectionRecord?> GetInspectionAsync(Guid bookingId, string kind, CancellationToken ct = default)
    {
        const string sql = """
            SELECT vehicle_inspection_id, booking_id, vehicle_id, kind, odometer_km, fuel_level, condition_notes, inspected_at
            FROM bonbon.vehicle_inspection
            WHERE booking_id = @booking_id AND kind = @kind
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("kind", kind);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new InspectionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }, ct);
    }

    public async Task<IReadOnlyList<InspectionRecord>> GetInspectionsByBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async connection => await LoadInspectionsAsync(connection, bookingId, ct), ct);
    }

    public async Task<IReadOnlyList<AdditionalChargeRecord>> GetChargesByBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        return await WithConnectionAsync(async connection => await LoadChargesAsync(connection, bookingId, ct), ct);
    }

    public async Task<InspectionRecord> CreateInspectionAsync(
        Guid bookingId,
        Guid vehicleId,
        string kind,
        decimal? odometerKm,
        decimal? fuelLevel,
        string? notes,
        DateTimeOffset? inspectedAt,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bonbon.vehicle_inspection (
                booking_id, vehicle_id, kind, odometer_km, fuel_level, condition_notes, inspected_at
            ) VALUES (
                @booking_id, @vehicle_id, @kind, @odometer_km, @fuel_level, @notes, COALESCE(@inspected_at, now())
            )
            ON CONFLICT (booking_id, kind) DO UPDATE SET
                odometer_km = EXCLUDED.odometer_km,
                fuel_level = EXCLUDED.fuel_level,
                condition_notes = EXCLUDED.condition_notes,
                inspected_at = EXCLUDED.inspected_at
            RETURNING vehicle_inspection_id, booking_id, vehicle_id, kind, odometer_km, fuel_level, condition_notes, inspected_at
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("vehicle_id", vehicleId);
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("odometer_km", (object?)odometerKm ?? DBNull.Value);
            command.Parameters.AddWithValue("fuel_level", (object?)fuelLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
            command.Parameters.AddWithValue("inspected_at", inspectedAt.HasValue ? inspectedAt.Value.ToUniversalTime() : DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Failed to save inspection.");

            return new InspectionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }, ct);
    }

    public async Task<AdditionalChargeRecord?> SaveExcessMileageChargeAsync(
        Guid bookingId,
        decimal excessKm,
        decimal feePerKm,
        decimal amount,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO bonbon.additional_charge (
                booking_id, reason_code, description, amount, currency, status, proposed_at
            ) VALUES (
                @booking_id, 'EXCESS_MILEAGE', @description, @amount, 'VND', 'Proposed', now()
            )
            ON CONFLICT (booking_id, reason_code) WHERE reason_code = 'EXCESS_MILEAGE'
            DO UPDATE SET
                description = EXCLUDED.description,
                amount = EXCLUDED.amount,
                proposed_at = EXCLUDED.proposed_at
            RETURNING additional_charge_id, booking_id, reason_code, description, amount, currency, status, proposed_at
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("description", $"Phí vượt quãng đường: vượt {excessKm:N1} km (đơn giá {feePerKm:N0} VND/km)");
            command.Parameters.AddWithValue("amount", amount);

            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;

            return new AdditionalChargeRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7));
        }, ct);
    }

    public async Task<bool> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        string[] allowedCurrentStatuses,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE bonbon.booking
            SET status = @new_status
            WHERE booking_id = @booking_id
              AND status = ANY(@allowed_statuses)
            """;

        return await WithConnectionAsync(async connection =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("new_status", newStatus);
            command.Parameters.AddWithValue("allowed_statuses", allowedCurrentStatuses);
            var affected = await command.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }, ct);
    }

    private static async Task<IReadOnlyList<InspectionRecord>> LoadInspectionsAsync(
        NpgsqlConnection connection,
        Guid bookingId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT vehicle_inspection_id, booking_id, vehicle_id, kind, odometer_km, fuel_level, condition_notes, inspected_at
            FROM bonbon.vehicle_inspection
            WHERE booking_id = @booking_id
            ORDER BY inspected_at ASC
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("booking_id", bookingId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<InspectionRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new InspectionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return list;
    }

    private static async Task<IReadOnlyList<AdditionalChargeRecord>> LoadChargesAsync(
        NpgsqlConnection connection,
        Guid bookingId,
        CancellationToken ct)
    {
        const string sql = """
            SELECT additional_charge_id, booking_id, reason_code, description, amount, currency, status, proposed_at
            FROM bonbon.additional_charge
            WHERE booking_id = @booking_id
            ORDER BY proposed_at ASC
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("booking_id", bookingId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<AdditionalChargeRecord>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdditionalChargeRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return list;
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
