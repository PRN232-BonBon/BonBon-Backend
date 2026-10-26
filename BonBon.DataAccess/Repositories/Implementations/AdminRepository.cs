using System.Data;
using System.Text.Json;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class AdminRepository(BonBonDbContext db) : IAdminRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<bool> IsActiveAdminAsync(Guid accountId, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT bonbon.has_active_role(@id, 1::smallint)", connection);
        command.Parameters.AddWithValue("id", accountId);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<IReadOnlyList<ReviewVehicle>> GetVehiclesAsync(bool publicOnly, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(item)), '[]'::jsonb)::text FROM (
                SELECT v.vehicle_id AS "id", v.brand, v.model, v.production_year AS "year",
                    v.plate_number AS "plateNumber", v.body_type AS "bodyType", v.seats,
                    v.transmission, v.fuel_type AS "fuel", v.pickup_address AS "pickupAddress",
                    v.description, v.features, v.hour_price AS "hourPrice", v.day_price AS "dayPrice", v.status,
                    a.full_name AS "ownerName", a.email AS "ownerEmail", a.phone AS "ownerPhone",
                    ARRAY(SELECT COALESCE(vi.image_url, '/api/vehicles/images/' || vi.vehicle_image_id::text)
                        FROM bonbon.vehicle_image vi WHERE vi.vehicle_id = v.vehicle_id
                        ORDER BY vi.is_cover DESC, vi.sort_order, vi.vehicle_image_id) AS "imageUrls",
                    v.weekly_discount_enabled AS "weeklyDiscountEnabled", v.weekly_discount_percent AS "weeklyDiscountPercent",
                    v.mileage_limit_enabled AS "mileageLimitEnabled", v.daily_mileage_limit_km AS "dailyMileageLimitKm",
                    v.overage_fee_per_km AS "overageFeePerKm", v.rental_terms AS "rentalTerms",
                    v.rejection_reason AS "rejectionReason", v.submitted_at AS "submittedAt", v.reviewed_at AS "reviewedAt"
                FROM bonbon.vehicle v JOIN bonbon.account a ON a.account_id = v.owner_account_id
                WHERE NOT @public_only OR (v.status = 'Available' AND bonbon.has_active_role(v.owner_account_id, 3::smallint))
                ORDER BY v.submitted_at DESC, v.vehicle_id
                LIMIT 500
            ) item
            """, connection);
        command.Parameters.AddWithValue("public_only", publicOnly);
        var json = (string)(await command.ExecuteScalarAsync(ct) ?? "[]");
        return JsonSerializer.Deserialize<List<ReviewVehicle>>(json, JsonOptions) ?? [];
    }

    public async Task<string> ReviewAsync(Guid adminId, Guid vehicleId, bool approve, string? reason, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        Guid ownerId;
        string status;
        await using (var command = new NpgsqlCommand("SELECT owner_account_id, status FROM bonbon.vehicle WHERE vehicle_id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", vehicleId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return "NOT_FOUND";
            ownerId = reader.GetGuid(0);
            status = reader.GetString(1);
        }
        if (status != "PendingApproval") return "ALREADY_REVIEWED";

        if (approve)
        {
            await using var owner = new NpgsqlCommand("""
                UPDATE bonbon.account_role ar SET status = 'Active'
                FROM bonbon.account a
                WHERE ar.account_id = @owner AND ar.role_id = 3 AND ar.status IN ('Pending', 'Active')
                    AND a.account_id = ar.account_id AND a.status = 'Active'
                """, connection, transaction);
            owner.Parameters.AddWithValue("owner", ownerId);
            if (await owner.ExecuteNonQueryAsync(ct) != 1) return "OWNER_INACTIVE";
            await using var images = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM bonbon.vehicle_image WHERE vehicle_id = @id)", connection, transaction);
            images.Parameters.AddWithValue("id", vehicleId);
            if (!(bool)(await images.ExecuteScalarAsync(ct) ?? false)) return "MISSING_IMAGES";
        }

        await using var update = new NpgsqlCommand("""
            UPDATE bonbon.vehicle SET status = @status, rejection_reason = @reason,
                reviewed_at = now(), reviewed_by = @admin WHERE vehicle_id = @id
            """, connection, transaction);
        update.Parameters.AddWithValue("id", vehicleId);
        update.Parameters.AddWithValue("status", approve ? "Available" : "Rejected");
        update.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        update.Parameters.AddWithValue("admin", adminId);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return "OK";
    }
}
