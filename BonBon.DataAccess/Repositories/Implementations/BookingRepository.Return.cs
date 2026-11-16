using System.Data;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

using BonBon.DataAccess.Repositories.Interfaces;

public sealed partial class BookingRepository
{
    public Task<InspectionRecord?> ConfirmPickupAtomicAsync(Guid bookingId, Guid accountId, bool isAdmin,
        decimal odometerKm, decimal? fuelLevel, string? notes, CancellationToken ct = default) => WithConnectionAsync(async connection =>
    {
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        Guid vehicleId;
        await using (var vehicleLock = new NpgsqlCommand("SELECT v.vehicle_id FROM bonbon.vehicle v JOIN bonbon.booking b ON b.vehicle_id=v.vehicle_id WHERE b.booking_id=@id AND (@admin OR b.customer_account_id=@account OR v.owner_account_id=@account) FOR UPDATE OF v", connection, tx))
        {
            vehicleLock.Parameters.AddWithValue("id", bookingId); vehicleLock.Parameters.AddWithValue("account", accountId); vehicleLock.Parameters.AddWithValue("admin", isAdmin);
            if (await vehicleLock.ExecuteScalarAsync(ct) is not Guid found) return null;
            vehicleId = found;
        }
        await using (var bookingLock = new NpgsqlCommand("SELECT status FROM bonbon.booking WHERE booking_id=@id FOR UPDATE", connection, tx))
        {
            bookingLock.Parameters.AddWithValue("id", bookingId);
            if ((string?)await bookingLock.ExecuteScalarAsync(ct) is not ("Confirmed" or "ReadyForPickup")) return null;
        }
        InspectionRecord inspection;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO bonbon.vehicle_inspection(booking_id,vehicle_id,kind,odometer_km,fuel_level,condition_notes)
            VALUES(@booking,@vehicle,'Pickup',@km,@fuel,@notes)
            RETURNING vehicle_inspection_id, inspected_at
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("booking", bookingId); insert.Parameters.AddWithValue("vehicle", vehicleId);
            insert.Parameters.AddWithValue("km", odometerKm); insert.Parameters.AddWithValue("fuel", (object?)fuelLevel ?? DBNull.Value);
            insert.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            inspection = new(reader.GetGuid(0), bookingId, vehicleId, "Pickup", odometerKm, fuelLevel, notes, reader.GetFieldValue<DateTimeOffset>(1));
        }
        await using (var update = new NpgsqlCommand("UPDATE bonbon.booking SET status='InProgress' WHERE booking_id=@id AND status IN ('Confirmed','ReadyForPickup')", connection, tx))
        {
            update.Parameters.AddWithValue("id", bookingId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        await tx.CommitAsync(ct);
        return inspection;
    }, ct);

    // The vehicle lock precedes the booking lock, matching the schedule/payment lock order.
    public Task<SubmittedReturn?> SubmitReturnAsync(Guid bookingId, Guid customerId, decimal odometerKm,
        decimal? fuelLevel, string? notes, CancellationToken ct = default) => WithConnectionAsync(async connection =>
    {
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var vehicleLock = new NpgsqlCommand("SELECT v.vehicle_id FROM bonbon.vehicle v JOIN bonbon.booking b ON b.vehicle_id=v.vehicle_id WHERE b.booking_id=@id FOR UPDATE OF v", connection, tx))
        {
            vehicleLock.Parameters.AddWithValue("id", bookingId);
            if (await vehicleLock.ExecuteScalarAsync(ct) is null) return null;
        }
        Guid vehicleId; decimal pickup; int? included; decimal? rate;
        await using (var select = new NpgsqlCommand("""
            SELECT b.vehicle_id, i.odometer_km, b.included_mileage_km_snapshot, b.overage_fee_per_km_snapshot
            FROM bonbon.booking b JOIN bonbon.vehicle_inspection i ON i.booking_id=b.booking_id AND i.kind='Pickup'
            WHERE b.booking_id=@id AND b.customer_account_id=@customer AND b.status='InProgress' FOR UPDATE OF b
            """, connection, tx))
        {
            select.Parameters.AddWithValue("id", bookingId); select.Parameters.AddWithValue("customer", customerId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(1)) return null;
            vehicleId = reader.GetGuid(0); pickup = reader.GetDecimal(1);
            included = reader.IsDBNull(2) ? null : reader.GetInt32(2); rate = reader.IsDBNull(3) ? null : reader.GetDecimal(3);
        }
        if (odometerKm < pickup || decimal.Round(odometerKm, 1) != odometerKm) return null;
        var distance = odometerKm - pickup;
        var excess = included is > 0 ? Math.Max(0, distance - included.Value) : 0;
        var fee = decimal.Round(excess * (rate ?? 0), 0, MidpointRounding.AwayFromZero);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO bonbon.vehicle_inspection(booking_id,vehicle_id,kind,odometer_km,fuel_level,condition_notes)
            VALUES(@booking,@vehicle,'Return',@km,@fuel,@notes)
            """, connection, tx))
        {
            insert.Parameters.AddWithValue("booking", bookingId); insert.Parameters.AddWithValue("vehicle", vehicleId);
            insert.Parameters.AddWithValue("km", odometerKm); insert.Parameters.AddWithValue("fuel", (object?)fuelLevel ?? DBNull.Value);
            insert.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value); await insert.ExecuteNonQueryAsync(ct);
        }
        Guid? chargeId = null;
        if (fee > 0)
        {
            await using var charge = new NpgsqlCommand("""
                INSERT INTO bonbon.additional_charge(booking_id,reason_code,description,amount,currency,status)
                VALUES(@booking,'EXCESS_MILEAGE',@description,@amount,'VND','Proposed') RETURNING additional_charge_id
                """, connection, tx);
            charge.Parameters.AddWithValue("booking", bookingId); charge.Parameters.AddWithValue("amount", fee);
            charge.Parameters.AddWithValue("description", $"Phí vượt quãng đường: vượt {excess:N1} km (đơn giá {rate:N0} VND/km)");
            chargeId = (Guid)(await charge.ExecuteScalarAsync(ct))!;
        }
        await using (var update = new NpgsqlCommand("UPDATE bonbon.booking SET status='Returned' WHERE booking_id=@id AND status='InProgress'", connection, tx))
        {
            update.Parameters.AddWithValue("id", bookingId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        await tx.CommitAsync(ct);
        return new SubmittedReturn(distance, included, excess, rate, fee, chargeId);
    }, ct);

    public Task<string?> ConfirmReturnByOwnerAsync(Guid bookingId, Guid ownerId, bool isAdmin, CancellationToken ct = default) => WithConnectionAsync(async connection =>
    {
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using (var vehicleLock = new NpgsqlCommand("SELECT v.vehicle_id FROM bonbon.vehicle v JOIN bonbon.booking b ON b.vehicle_id=v.vehicle_id WHERE b.booking_id=@id AND (@admin OR v.owner_account_id=@owner) FOR UPDATE OF v", connection, tx))
        {
            vehicleLock.Parameters.AddWithValue("id", bookingId); vehicleLock.Parameters.AddWithValue("owner", ownerId); vehicleLock.Parameters.AddWithValue("admin", isAdmin);
            if (await vehicleLock.ExecuteScalarAsync(ct) is null) return null;
        }
        await using (var bookingLock = new NpgsqlCommand("SELECT status FROM bonbon.booking WHERE booking_id=@id FOR UPDATE", connection, tx))
        {
            bookingLock.Parameters.AddWithValue("id", bookingId);
            if ((string?)await bookingLock.ExecuteScalarAsync(ct) != "Returned") return null;
        }
        await using (var accept = new NpgsqlCommand("UPDATE bonbon.additional_charge SET status='Accepted' WHERE booking_id=@id AND status='Proposed' AND reason_code='EXCESS_MILEAGE'", connection, tx))
        { accept.Parameters.AddWithValue("id", bookingId); await accept.ExecuteNonQueryAsync(ct); }
        bool unpaid;
        await using (var pending = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM bonbon.additional_charge WHERE booking_id=@id AND status IN ('Proposed','Accepted'))", connection, tx))
        { pending.Parameters.AddWithValue("id", bookingId); unpaid = (bool)(await pending.ExecuteScalarAsync(ct))!; }
        var status = unpaid ? "AwaitingSettlement" : "Completed";
        await using (var update = new NpgsqlCommand("UPDATE bonbon.booking SET status=@status WHERE booking_id=@id AND status='Returned'", connection, tx))
        { update.Parameters.AddWithValue("id", bookingId); update.Parameters.AddWithValue("status", status); await update.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return status;
    }, ct);
}
