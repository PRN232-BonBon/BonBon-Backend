namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record ReviewVehicle(
    Guid Id, string Brand, string Model, int? Year, string PlateNumber,
    string BodyType, int Seats, string Transmission, string Fuel,
    string PickupAddress, string? Description, string[] Features,
    decimal HourPrice, decimal DayPrice, string Status,
    string OwnerName, string OwnerEmail, string? OwnerPhone,
    string[] ImageUrls, bool WeeklyDiscountEnabled, decimal? WeeklyDiscountPercent,
    bool MileageLimitEnabled, int? DailyMileageLimitKm, decimal? OverageFeePerKm,
    string? RentalTerms, string? RejectionReason, DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt);

public interface IAdminRepository
{
    Task<bool> IsActiveAdminAsync(Guid accountId, CancellationToken ct);
    Task<IReadOnlyList<ReviewVehicle>> GetVehiclesAsync(bool publicOnly, CancellationToken ct);
    // Returns OK, NOT_FOUND, ALREADY_REVIEWED, OWNER_INACTIVE or MISSING_IMAGES.
    Task<string> ReviewAsync(Guid adminId, Guid vehicleId, bool approve, string? reason, CancellationToken ct);
}
