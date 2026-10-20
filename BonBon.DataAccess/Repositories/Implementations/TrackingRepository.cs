using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class TrackingRepository(BonBonDbContext db) : ITrackingRepository
{
    private readonly BonBonDbContext _db = db;

    public Task<TrackingBooking?> GetBookingAsync(Guid bookingId, CancellationToken ct = default) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                SELECT b.booking_id, b.customer_account_id, v.owner_account_id, v.traccar_device_id,
                       b.status, b.pickup_meeting_latitude, b.pickup_meeting_longitude,
                       b.return_meeting_latitude, b.return_meeting_longitude
                FROM bonbon.booking b
                JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
                WHERE b.booking_id = @booking_id
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new TrackingBooking(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7), reader.IsDBNull(8) ? null : reader.GetDecimal(8));
        }, ct);

    public Task<bool> SetMeetingPointsAsync(Guid bookingId, Guid ownerAccountId, decimal pickupLatitude,
        decimal pickupLongitude, decimal returnLatitude, decimal returnLongitude, CancellationToken ct = default) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                UPDATE bonbon.booking b SET
                    pickup_meeting_latitude = @pickup_latitude,
                    pickup_meeting_longitude = @pickup_longitude,
                    return_meeting_latitude = @return_latitude,
                    return_meeting_longitude = @return_longitude
                FROM bonbon.vehicle v
                WHERE b.booking_id = @booking_id AND b.vehicle_id = v.vehicle_id
                  AND v.owner_account_id = @owner_account_id
                  AND b.status IN ('Confirmed', 'ReadyForPickup')
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("owner_account_id", ownerAccountId);
            command.Parameters.AddWithValue("pickup_latitude", pickupLatitude);
            command.Parameters.AddWithValue("pickup_longitude", pickupLongitude);
            command.Parameters.AddWithValue("return_latitude", returnLatitude);
            command.Parameters.AddWithValue("return_longitude", returnLongitude);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    public Task<bool> SetDeviceAsync(Guid bookingId, Guid ownerAccountId, long deviceId, CancellationToken ct = default) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                UPDATE bonbon.vehicle v SET traccar_device_id = @device_id
                FROM bonbon.booking b
                WHERE b.booking_id = @booking_id AND b.vehicle_id = v.vehicle_id
                  AND v.owner_account_id = @owner_account_id
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("owner_account_id", ownerAccountId);
            command.Parameters.AddWithValue("device_id", deviceId);
            return await command.ExecuteNonQueryAsync(ct) == 1;
        }, ct);

    public Task<IReadOnlyList<TrackedBookingTarget>> GetTargetsForDeviceAsync(long deviceId, CancellationToken ct = default) =>
        WithConnectionAsync<IReadOnlyList<TrackedBookingTarget>>(async connection =>
        {
            const string sql = """
                SELECT b.booking_id, b.customer_account_id, v.owner_account_id, b.status,
                       CASE WHEN b.status = 'ReadyForPickup' THEN b.pickup_meeting_latitude ELSE b.return_meeting_latitude END,
                       CASE WHEN b.status = 'ReadyForPickup' THEN b.pickup_meeting_longitude ELSE b.return_meeting_longitude END
                FROM bonbon.booking b
                JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
                WHERE v.traccar_device_id = @device_id
                  AND ((b.status = 'ReadyForPickup' AND b.pickup_meeting_latitude IS NOT NULL)
                    OR (b.status = 'InProgress' AND b.return_meeting_latitude IS NOT NULL))
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("device_id", deviceId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var targets = new List<TrackedBookingTarget>();
            while (await reader.ReadAsync(ct))
                targets.Add(new TrackedBookingTarget(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2),
                    reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5)));
            return targets;
        }, ct);

    public async Task SavePositionAsync(Guid bookingId, decimal latitude, decimal longitude, decimal? accuracyMeters,
        DateTimeOffset recordedAt, CancellationToken ct = default)
    {
        await WithConnectionAsync(async connection =>
        {
            const string sql = """
                INSERT INTO bonbon.vehicle_location (booking_id, latitude, longitude, accuracy_meters, source, recorded_at)
                VALUES (@booking_id, @latitude, @longitude, @accuracy, 'Traccar', @recorded_at)
                ON CONFLICT DO NOTHING
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            command.Parameters.AddWithValue("latitude", latitude);
            command.Parameters.AddWithValue("longitude", longitude);
            command.Parameters.AddWithValue("accuracy", (object?)accuracyMeters ?? DBNull.Value);
            command.Parameters.AddWithValue("recorded_at", recordedAt.ToUniversalTime());
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }, ct);
    }

    public Task<StoredVehiclePosition?> GetLatestPositionAsync(Guid bookingId, CancellationToken ct = default) =>
        WithConnectionAsync(async connection =>
        {
            const string sql = """
                SELECT latitude, longitude, accuracy_meters, recorded_at
                FROM bonbon.vehicle_location
                WHERE booking_id = @booking_id
                ORDER BY recorded_at DESC LIMIT 1
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("booking_id", bookingId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new StoredVehiclePosition(reader.GetDecimal(0), reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2), reader.GetFieldValue<DateTimeOffset>(3));
        }, ct);

    private async Task<T> WithConnectionAsync<T>(Func<NpgsqlConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try { return await action(connection); }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }
}
