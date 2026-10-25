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
                vehicleCommand.Parameters.AddWithValue("plate_number", request.PlateNumber.Trim().ToUpperInvariant());
                vehicleCommand.Parameters.AddWithValue("brand", request.Brand.Trim());
                vehicleCommand.Parameters.AddWithValue("model", request.Model.Trim());
                vehicleCommand.Parameters.AddWithValue("production_year", request.ProductionYear);
                vehicleCommand.Parameters.AddWithValue("body_type", request.BodyType.Trim());
                vehicleCommand.Parameters.AddWithValue("seats", request.Seats);
                vehicleCommand.Parameters.AddWithValue("transmission", request.Transmission.Trim());
                vehicleCommand.Parameters.AddWithValue("fuel_type", request.FuelType.Trim());
                vehicleCommand.Parameters.AddWithValue("pickup_address", request.PickupAddress.Trim());
                vehicleCommand.Parameters.AddWithValue("hour_price", request.HourPrice);
                vehicleCommand.Parameters.AddWithValue("day_price", request.DayPrice);
                vehicleCommand.Parameters.AddWithValue("description", request.Description.Trim());
                vehicleCommand.Parameters.AddWithValue("features", request.Features.Distinct().Take(20).ToArray());
                vehicleCommand.Parameters.AddWithValue("weekly_discount_enabled", request.WeeklyDiscountEnabled);
                vehicleCommand.Parameters.AddWithValue("weekly_discount_percent", (object?)request.WeeklyDiscountPercent ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("mileage_limit_enabled", request.MileageLimitEnabled);
                vehicleCommand.Parameters.AddWithValue("daily_mileage_limit_km", (object?)request.DailyMileageLimitKm ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("overage_fee_per_km", (object?)request.OverageFeePerKm ?? DBNull.Value);
                vehicleCommand.Parameters.AddWithValue("rental_terms", string.IsNullOrWhiteSpace(request.RentalTerms) ? DBNull.Value : request.RentalTerms.Trim());
                vehicleId = (Guid)(await vehicleCommand.ExecuteScalarAsync(ct)
                    ?? throw new InvalidOperationException("Database did not return the vehicle id."));
            }

            for (var index = 0; index < submission.VehicleImages.Count; index++)
            {
                var image = submission.VehicleImages[index];
                await using var imageCommand = new NpgsqlCommand("""
                    INSERT INTO bonbon.vehicle_image (
                        vehicle_id, image_data, content_type, byte_length, width, height, sort_order, is_cover
                    ) VALUES (
                        @vehicle_id, @image_data, 'image/webp', @byte_length, @width, @height, @sort_order, @is_cover
                    )
                    """, connection, transaction);
                imageCommand.Parameters.AddWithValue("vehicle_id", vehicleId);
                imageCommand.Parameters.AddWithValue("image_data", image.Data);
                imageCommand.Parameters.AddWithValue("byte_length", image.Data.Length);
                imageCommand.Parameters.AddWithValue("width", image.Width);
                imageCommand.Parameters.AddWithValue("height", image.Height);
                imageCommand.Parameters.AddWithValue("sort_order", index);
                imageCommand.Parameters.AddWithValue("is_cover", index == 0);
                await imageCommand.ExecuteNonQueryAsync(ct);
            }

            var document = submission.RegistrationDocument;
            await using (var documentCommand = new NpgsqlCommand("""
                INSERT INTO bonbon.vehicle_document (
                    vehicle_id, document_type, document_data, content_type, byte_length, width, height
                ) VALUES (
                    @vehicle_id, 'RegistrationCertificate', @document_data, 'image/webp', @byte_length, @width, @height
                )
                """, connection, transaction))
            {
                documentCommand.Parameters.AddWithValue("vehicle_id", vehicleId);
                documentCommand.Parameters.AddWithValue("document_data", document.Data);
                documentCommand.Parameters.AddWithValue("byte_length", document.Data.Length);
                documentCommand.Parameters.AddWithValue("width", document.Width);
                documentCommand.Parameters.AddWithValue("height", document.Height);
                await documentCommand.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            return new RegisteredVehicle(vehicleId, "PendingApproval", submission.VehicleImages.Count);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
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
}
