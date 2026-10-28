using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class VehicleRepository(BonBonDbContext db) : IVehicleRepository
{
    private readonly BonBonDbContext _db = db;

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
                       count(vi.vehicle_image_id)::integer
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
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6)));
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
              AND (@location IS NULL OR v.pickup_address ILIKE '%' || @location || '%')
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
            command.Parameters.AddWithValue("location", (object?)filter.Location ?? DBNull.Value);
            command.Parameters.AddWithValue("seats", (object?)filter.Seats ?? DBNull.Value);
            command.Parameters.AddWithValue("transmission", (object?)filter.Transmission ?? DBNull.Value);
            command.Parameters.AddWithValue("fuel_type", (object?)filter.FuelType ?? DBNull.Value);
            command.Parameters.AddWithValue("body_type", (object?)filter.BodyType ?? DBNull.Value);
            command.Parameters.AddWithValue("min_price", (object?)filter.MinPrice ?? DBNull.Value);
            command.Parameters.AddWithValue("max_price", (object?)filter.MaxPrice ?? DBNull.Value);
            command.Parameters.AddWithValue("start_at", filter.StartAt.HasValue ? filter.StartAt.Value.ToUniversalTime() : DBNull.Value);
            command.Parameters.AddWithValue("end_at", filter.EndAt.HasValue ? filter.EndAt.Value.ToUniversalTime() : DBNull.Value);

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
