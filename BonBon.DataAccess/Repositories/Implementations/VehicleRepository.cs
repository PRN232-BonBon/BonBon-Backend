using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class VehicleRepository(BonBonDbContext db) : IVehicleRepository
{
    private readonly BonBonDbContext _db = db;

    public async Task<string?> GetRejectedRegistrationJsonAsync(Guid ownerAccountId, Guid vehicleId, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT json_build_object(
                  'vehicleId', v.vehicle_id, 'rejectionReason', v.rejection_reason,
                  'plateNumber', v.plate_number, 'brand', v.brand, 'model', v.model,
                  'productionYear', v.production_year, 'seats', v.seats, 'transmission', v.transmission,
                  'fuelType', v.fuel_type, 'bodyType', v.body_type, 'description', v.description,
                  'pickupAddress', v.pickup_address, 'hourPrice', v.hour_price, 'dayPrice', v.day_price,
                  'weeklyDiscountEnabled', v.weekly_discount_enabled, 'weeklyDiscountPercent', v.weekly_discount_percent,
                  'mileageLimitEnabled', v.mileage_limit_enabled, 'dailyMileageLimitKm', v.daily_mileage_limit_km,
                  'overageFeePerKm', v.overage_fee_per_km, 'rentalTerms', v.rental_terms, 'features', v.features,
                  'imageUrls', COALESCE((SELECT json_agg(COALESCE(i.image_url, '/api/vehicles/images/' || i.vehicle_image_id::text)
                    ORDER BY i.is_cover DESC, i.sort_order, i.vehicle_image_id) FROM bonbon.vehicle_image i WHERE i.vehicle_id = v.vehicle_id), '[]'::json)
                )::text FROM bonbon.vehicle v
                WHERE v.vehicle_id = @vehicle AND v.owner_account_id = @owner AND v.status = 'Rejected'
                  AND EXISTS (SELECT 1 FROM bonbon.account a WHERE a.account_id = @owner AND a.status = 'Active')
                """, connection);
            command.Parameters.AddWithValue("vehicle", vehicleId);
            command.Parameters.AddWithValue("owner", ownerAccountId);
            return await command.ExecuteScalarAsync(ct) as string;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    public async Task<RegisteredVehicle?> ResubmitAsync(Guid ownerAccountId, Guid vehicleId, VehicleRegistrationSubmission submission, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            var data = submission.Vehicle;
            await using (var command = new NpgsqlCommand("""
                UPDATE bonbon.vehicle SET plate_number=@plate, brand=@brand, model=@model, production_year=@year,
                  seats=@seats, transmission=@transmission, fuel_type=@fuel, body_type=@body, description=@description,
                  pickup_address=@address, hour_price=@hour, day_price=@day, weekly_discount_enabled=@discount_enabled,
                  weekly_discount_percent=@discount, mileage_limit_enabled=@mileage_enabled, daily_mileage_limit_km=@mileage,
                  overage_fee_per_km=@overage, rental_terms=@terms, features=@features,
                  status='PendingApproval', submitted_at=now(), reviewed_at=NULL, reviewed_by=NULL
                WHERE vehicle_id=@vehicle AND owner_account_id=@owner AND status='Rejected'
                  AND EXISTS (SELECT 1 FROM bonbon.account a WHERE a.account_id=@owner AND a.status='Active')
                  AND (@has_images OR EXISTS(SELECT 1 FROM bonbon.vehicle_image WHERE vehicle_id=@vehicle))
                RETURNING vehicle_id
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("vehicle", vehicleId); command.Parameters.AddWithValue("owner", ownerAccountId);
                command.Parameters.AddWithValue("has_images", submission.VehicleImages.Count > 0);
                command.Parameters.AddWithValue("plate", data.PlateNumber); command.Parameters.AddWithValue("brand", data.Brand);
                command.Parameters.AddWithValue("model", data.Model); command.Parameters.AddWithValue("year", data.ProductionYear);
                command.Parameters.AddWithValue("seats", data.Seats); command.Parameters.AddWithValue("transmission", data.Transmission);
                command.Parameters.AddWithValue("fuel", data.FuelType); command.Parameters.AddWithValue("body", data.BodyType);
                command.Parameters.AddWithValue("description", data.Description); command.Parameters.AddWithValue("address", data.PickupAddress);
                command.Parameters.AddWithValue("hour", data.HourPrice); command.Parameters.AddWithValue("day", data.DayPrice);
                command.Parameters.AddWithValue("discount_enabled", data.WeeklyDiscountEnabled);
                command.Parameters.AddWithValue("discount", (object?)data.WeeklyDiscountPercent ?? DBNull.Value);
                command.Parameters.AddWithValue("mileage_enabled", data.MileageLimitEnabled);
                command.Parameters.AddWithValue("mileage", (object?)data.DailyMileageLimitKm ?? DBNull.Value);
                command.Parameters.AddWithValue("overage", (object?)data.OverageFeePerKm ?? DBNull.Value);
                command.Parameters.AddWithValue("terms", (object?)data.RentalTerms ?? DBNull.Value);
                command.Parameters.AddWithValue("features", data.Features);
                if (await command.ExecuteScalarAsync(ct) is null) return null;
            }

            if (submission.VehicleImages.Count > 0)
            {
                await using var remove = new NpgsqlCommand("DELETE FROM bonbon.vehicle_image WHERE vehicle_id=@vehicle", connection, transaction);
                remove.Parameters.AddWithValue("vehicle", vehicleId);
                await remove.ExecuteNonQueryAsync(ct);
                for (var i = 0; i < submission.VehicleImages.Count; i++)
                {
                    var image = submission.VehicleImages[i];
                    await using var insert = new NpgsqlCommand("""
                        INSERT INTO bonbon.vehicle_image(vehicle_id, is_cover, sort_order, image_data, content_type, byte_length, width, height)
                        VALUES (@vehicle, @cover, @order, @data, 'image/webp', @length, @width, @height)
                        """, connection, transaction);
                    insert.Parameters.AddWithValue("vehicle", vehicleId); insert.Parameters.AddWithValue("cover", i == 0);
                    insert.Parameters.AddWithValue("order", (short)i); insert.Parameters.AddWithValue("data", image.Data);
                    insert.Parameters.AddWithValue("length", image.Data.Length); insert.Parameters.AddWithValue("width", image.Width);
                    insert.Parameters.AddWithValue("height", image.Height);
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            await using var count = new NpgsqlCommand("SELECT count(*)::integer FROM bonbon.vehicle_image WHERE vehicle_id=@vehicle", connection, transaction);
            count.Parameters.AddWithValue("vehicle", vehicleId);
            var imageCount = (int)(await count.ExecuteScalarAsync(ct) ?? 0);
            await transaction.CommitAsync(ct);
            return new RegisteredVehicle(vehicleId, "PendingApproval", imageCount);
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    public async Task<RegisteredVehicle> RegisterAsync(
        Guid ownerAccountId,
        VehicleRegistrationSubmission submission,
        CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        try
        {
            await using (var roleCommand = new NpgsqlCommand("""
                INSERT INTO bonbon.account_role (account_id, role_id, status)
                VALUES (@account_id, 3, 'Pending')
                ON CONFLICT (account_id, role_id) DO NOTHING
                """, connection, transaction))
            {
                roleCommand.Parameters.AddWithValue("account_id", ownerAccountId);
                await roleCommand.ExecuteNonQueryAsync(ct);
            }

            var request = submission.Vehicle;
            Guid vehicleId;
            await using (var vehicleCommand = new NpgsqlCommand("""
                INSERT INTO bonbon.vehicle (
                    owner_account_id, plate_number, brand, model, production_year, body_type,
                    seats, transmission, fuel_type, pickup_address, hour_price, day_price,
                    description, features, weekly_discount_enabled, weekly_discount_percent,
                    mileage_limit_enabled, daily_mileage_limit_km, overage_fee_per_km,
                    rental_terms, status
                ) VALUES (
                    @owner_account_id, @plate_number, @brand, @model, @production_year, @body_type,
                    @seats, @transmission, @fuel_type, @pickup_address, @hour_price, @day_price,
                    @description, @features, @weekly_discount_enabled, @weekly_discount_percent,
                    @mileage_limit_enabled, @daily_mileage_limit_km, @overage_fee_per_km,
                    @rental_terms, 'PendingApproval'
                ) RETURNING vehicle_id
                """, connection, transaction))
            {
                vehicleCommand.Parameters.AddWithValue("owner_account_id", ownerAccountId);
                vehicleCommand.Parameters.AddWithValue("plate_number", request.PlateNumber);
                vehicleCommand.Parameters.AddWithValue("brand", request.Brand);
                vehicleCommand.Parameters.AddWithValue("model", request.Model);
                vehicleCommand.Parameters.AddWithValue("production_year", request.ProductionYear);
                vehicleCommand.Parameters.AddWithValue("body_type", request.BodyType);
                vehicleCommand.Parameters.AddWithValue("seats", request.Seats);
                vehicleCommand.Parameters.AddWithValue("transmission", request.Transmission);
                vehicleCommand.Parameters.AddWithValue("fuel_type", request.FuelType);
                vehicleCommand.Parameters.AddWithValue("pickup_address", request.PickupAddress);
                vehicleCommand.Parameters.AddWithValue("hour_price", request.HourPrice);
                vehicleCommand.Parameters.AddWithValue("day_price", request.DayPrice);
                vehicleCommand.Parameters.AddWithValue("description", request.Description);
                vehicleCommand.Parameters.AddWithValue("features", request.Features);
                vehicleCommand.Parameters.AddWithValue("weekly_discount_enabled", request.WeeklyDiscountEnabled);
                vehicleCommand.Parameters.AddWithValue("weekly_discount_percent", (object?)request.WeeklyDiscountPercent ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("mileage_limit_enabled", request.MileageLimitEnabled);
                vehicleCommand.Parameters.AddWithValue("daily_mileage_limit_km", (object?)request.DailyMileageLimitKm ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("overage_fee_per_km", (object?)request.OverageFeePerKm ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("rental_terms", (object?)request.RentalTerms ?? DBNull.Value);
                vehicleId = (Guid)(await vehicleCommand.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Failed to register vehicle."));
            }

            for (var i = 0; i < submission.VehicleImages.Count; i++)
            {
                var image = submission.VehicleImages[i];
                await using var imageCommand = new NpgsqlCommand("""
                    INSERT INTO bonbon.vehicle_image (
                        vehicle_id, is_cover, sort_order, image_data, content_type,
                        byte_length, width, height
                    ) VALUES (
                        @vehicle_id, @is_cover, @sort_order, @image_data, 'image/webp',
                        @byte_length, @width, @height
                    )
                    """, connection, transaction);
                imageCommand.Parameters.AddWithValue("vehicle_id", vehicleId);
                imageCommand.Parameters.AddWithValue("is_cover", i == 0);
                imageCommand.Parameters.AddWithValue("sort_order", (short)i);
                imageCommand.Parameters.AddWithValue("image_data", image.Data);
                imageCommand.Parameters.AddWithValue("byte_length", image.Data.Length);
                imageCommand.Parameters.AddWithValue("width", image.Width);
                imageCommand.Parameters.AddWithValue("height", image.Height);
                await imageCommand.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return new RegisteredVehicle(vehicleId, "PendingApproval", submission.VehicleImages.Count);
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<OwnerVehicle>> GetMineAsync(Guid ownerAccountId, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT v.vehicle_id, v.brand, v.model, v.plate_number, v.status, v.rejection_reason,
                       count(vi.vehicle_image_id)::integer, v.production_year, v.seats, v.transmission,
                       v.body_type, v.pickup_address, v.hour_price, v.day_price,
                       (SELECT COALESCE(photo.image_url, '/api/vehicles/images/' || photo.vehicle_image_id::text)
                        FROM bonbon.vehicle_image photo WHERE photo.vehicle_id = v.vehicle_id
                        ORDER BY photo.is_cover DESC, photo.sort_order, photo.vehicle_image_id LIMIT 1)
                FROM bonbon.vehicle v
                LEFT JOIN bonbon.vehicle_image vi ON vi.vehicle_id = v.vehicle_id
                WHERE v.owner_account_id = @owner_account_id
                GROUP BY v.vehicle_id
                ORDER BY v.vehicle_id DESC
                """, connection);
            command.Parameters.AddWithValue("owner_account_id", ownerAccountId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<OwnerVehicle>();
            while (await reader.ReadAsync(ct))
            {
                items.Add(new OwnerVehicle(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt16(7), reader.GetInt16(8), reader.GetString(9),
                    reader.IsDBNull(10) ? "" : reader.GetString(10), reader.GetString(11), reader.GetDecimal(12), reader.GetDecimal(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
            return items;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    public async Task<(byte[] Data, string ContentType)?> GetImageAsync(Guid imageId, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT image_data, content_type FROM bonbon.vehicle_image
                WHERE vehicle_image_id = @image_id AND image_data IS NOT NULL
                """, connection);
            command.Parameters.AddWithValue("image_id", imageId);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            return await reader.ReadAsync(ct) ? (reader.GetFieldValue<byte[]>(0), reader.GetString(1)) : null;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<OwnerAvailabilityBlock>> GetOwnerBlocksAsync(
        Guid ownerAccountId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT ab.availability_block_id, ab.vehicle_id, ab.start_at, ab.end_at, ab.reason
                FROM bonbon.availability_block ab
                JOIN bonbon.vehicle v ON v.vehicle_id = ab.vehicle_id
                WHERE v.owner_account_id = @owner AND ab.start_at < @end AND ab.end_at > @start
                ORDER BY ab.start_at, ab.availability_block_id
                """, connection);
            command.Parameters.AddWithValue("owner", NpgsqlDbType.Uuid, ownerAccountId);
            command.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, startAt.ToUniversalTime());
            command.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, endAt.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<OwnerAvailabilityBlock>();
            while (await reader.ReadAsync(ct))
                items.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            return items;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    public async Task<IReadOnlyList<OccupiedVehicleInterval>> GetOccupiedIntervalsAsync(Guid vehicleId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand("""
                SELECT b.start_at, b.end_at FROM bonbon.booking b
                JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
                WHERE b.vehicle_id = @vehicle AND v.status = 'Available' AND bonbon.has_active_role(v.owner_account_id, 3::smallint)
                    AND b.status IN ('AwaitingPayment', 'Confirmed',
                    'ReadyForPickup', 'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
                    AND b.start_at < @end AND b.end_at > @start
                UNION ALL
                SELECT ab.start_at, ab.end_at FROM bonbon.availability_block ab
                JOIN bonbon.vehicle v ON v.vehicle_id = ab.vehicle_id
                WHERE ab.vehicle_id = @vehicle AND v.status = 'Available' AND bonbon.has_active_role(v.owner_account_id, 3::smallint)
                    AND ab.start_at < @end AND ab.end_at > @start
                ORDER BY start_at
                """, connection);
            command.Parameters.AddWithValue("vehicle", vehicleId);
            command.Parameters.AddWithValue("start", NpgsqlDbType.TimestampTz, startAt.ToUniversalTime());
            command.Parameters.AddWithValue("end", NpgsqlDbType.TimestampTz, endAt.ToUniversalTime());
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<OccupiedVehicleInterval>();
            while (await reader.ReadAsync(ct))
                result.Add(new(reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1)));
            return result;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    public async Task<IReadOnlyList<VehicleCatalogRecord>> SearchAsync(VehicleSearchFilter filter, CancellationToken ct = default)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(ct);

        const string sql = """
            SELECT v.vehicle_id, v.brand, v.model, v.production_year,
                   v.plate_number, v.body_type, v.seats, v.transmission, v.fuel_type,
                   v.pickup_address, v.description, v.features, v.hour_price, v.day_price, v.status,
                   a.account_id, a.full_name, a.email, a.phone,
                   ARRAY(
                       SELECT COALESCE(vi.image_url, '/api/vehicles/images/' || vi.vehicle_image_id::text)
                       FROM bonbon.vehicle_image vi
                       WHERE vi.vehicle_id = v.vehicle_id
                       ORDER BY vi.is_cover DESC, vi.sort_order, vi.vehicle_image_id
                   ) AS image_urls,
                   v.weekly_discount_enabled, v.weekly_discount_percent,
                   v.mileage_limit_enabled, v.daily_mileage_limit_km,
                   v.overage_fee_per_km, v.rental_terms
            FROM bonbon.vehicle v
            JOIN bonbon.account a ON a.account_id = v.owner_account_id
            WHERE v.status = 'Available'
              AND bonbon.has_active_role(v.owner_account_id, 3::smallint)
              AND (@location IS NULL OR v.pickup_address ILIKE '%' || @location || '%'
                   OR (@location = 'TP. Hồ Chí Minh' AND (
                       v.pickup_address ILIKE '%Hồ Chí Minh%'
                       OR v.pickup_address ILIKE '%HCM%'
                       OR v.pickup_address ILIKE '%Thủ Đức%')))
              AND (@seats IS NULL OR v.seats = @seats)
              AND (@transmission IS NULL OR v.transmission = @transmission)
              AND (@fuel_type IS NULL OR v.fuel_type = @fuel_type)
              AND (@body_type IS NULL OR v.body_type = @body_type)
              AND (@min_price IS NULL OR v.day_price >= @min_price)
              AND (@max_price IS NULL OR v.day_price <= @max_price)
              AND (@start_at IS NULL OR @end_at IS NULL OR (
                  NOT EXISTS (
                      SELECT 1 FROM bonbon.availability_block ab
                      WHERE ab.vehicle_id = v.vehicle_id
                        AND tstzrange(ab.start_at, ab.end_at, '[)') && tstzrange(@start_at, @end_at, '[)')
                  )
                  AND NOT EXISTS (
                      SELECT 1 FROM bonbon.booking b
                      WHERE b.vehicle_id = v.vehicle_id
                        AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
                            'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
                        AND tstzrange(b.start_at, b.end_at, '[)') && tstzrange(@start_at, @end_at, '[)')
                  )
              ))
            ORDER BY v.vehicle_id;
            """;

        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            // PostgreSQL cannot infer a NULL parameter's type from "@parameter IS NULL".
            // Keep the types explicit for both empty and partially populated filters.
            command.Parameters.Add("location", NpgsqlDbType.Text).Value = (object?)filter.Location ?? DBNull.Value;
            command.Parameters.Add("seats", NpgsqlDbType.Smallint).Value = (object?)filter.Seats ?? DBNull.Value;
            command.Parameters.Add("transmission", NpgsqlDbType.Text).Value = (object?)filter.Transmission ?? DBNull.Value;
            command.Parameters.Add("fuel_type", NpgsqlDbType.Text).Value = (object?)filter.FuelType ?? DBNull.Value;
            command.Parameters.Add("body_type", NpgsqlDbType.Text).Value = (object?)filter.BodyType ?? DBNull.Value;
            command.Parameters.Add("min_price", NpgsqlDbType.Numeric).Value = (object?)filter.MinPrice ?? DBNull.Value;
            command.Parameters.Add("max_price", NpgsqlDbType.Numeric).Value = (object?)filter.MaxPrice ?? DBNull.Value;
            command.Parameters.Add("start_at", NpgsqlDbType.TimestampTz).Value = (object?)filter.StartAt?.ToUniversalTime() ?? DBNull.Value;
            command.Parameters.Add("end_at", NpgsqlDbType.TimestampTz).Value = (object?)filter.EndAt?.ToUniversalTime() ?? DBNull.Value;

            await using var reader = await command.ExecuteReaderAsync(ct);
            var results = new List<VehicleCatalogRecord>();
            while (await reader.ReadAsync(ct))
            {
                results.Add(new VehicleCatalogRecord(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt16(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt16(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.GetFieldValue<string[]>(11),
                    reader.GetDecimal(12),
                    reader.GetDecimal(13),
                    reader.GetString(14),
                    reader.GetGuid(15),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.GetFieldValue<string[]>(19),
                    reader.GetBoolean(20),
                    reader.IsDBNull(21) ? null : reader.GetDecimal(21),
                    reader.GetBoolean(22),
                    reader.IsDBNull(23) ? null : reader.GetInt32(23),
                    reader.IsDBNull(24) ? null : reader.GetDecimal(24),
                    reader.IsDBNull(25) ? null : reader.GetString(25)));
            }
            return results;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }
}
