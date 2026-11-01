namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record StoredImage(byte[] Data, int Width, int Height);

public sealed record VehicleRegistrationData(
    string PlateNumber,
    string Brand,
    string Model,
    short ProductionYear,
    short Seats,
    string Transmission,
    string FuelType,
    string BodyType,
    string Description,
    string PickupAddress,
    decimal HourPrice,
    decimal DayPrice,
    bool WeeklyDiscountEnabled,
    decimal? WeeklyDiscountPercent,
    bool MileageLimitEnabled,
    int? DailyMileageLimitKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    string[] Features);

public sealed record VehicleRegistrationSubmission(
    VehicleRegistrationData Vehicle,
    IReadOnlyList<StoredImage> VehicleImages);

public sealed record RegisteredVehicle(Guid VehicleId, string Status, int ImageCount);
public sealed record OwnerVehicle(
    Guid VehicleId,
    string Brand,
    string Model,
    string PlateNumber,
    string Status,
    string? RejectionReason,
    int ImageCount);

public sealed record VehicleSearchFilter(
    string? Location,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    short? Seats,
    string? Transmission,
    string? FuelType,
    string? BodyType,
    decimal? MinPrice,
    decimal? MaxPrice);

public sealed record VehicleCatalogRecord(
    Guid Id,
    string Brand,
    string Model,
    int? Year,
    string PlateNumber,
    string BodyType,
    int Seats,
    string Transmission,
    string Fuel,
    string PickupAddress,
    string? Description,
    string[] Features,
    decimal HourPrice,
    decimal DayPrice,
    string Status,
    Guid OwnerId,
    string OwnerName,
    string OwnerEmail,
    string? OwnerPhone,
    string[] ImageUrls,
    bool WeeklyDiscountEnabled,
    decimal? WeeklyDiscountPercent,
    bool MileageLimitEnabled,
    int? DailyMileageLimitKm,
    decimal? OverageFeePerKm,
    string? RentalTerms);

public interface IVehicleRepository
{
    Task<RegisteredVehicle> RegisterAsync(
        Guid ownerAccountId,
        VehicleRegistrationSubmission submission,
        CancellationToken ct = default);

    Task<IReadOnlyList<OwnerVehicle>> GetMineAsync(Guid ownerAccountId, CancellationToken ct = default);
    Task<(byte[] Data, string ContentType)?> GetImageAsync(Guid imageId, CancellationToken ct = default);
    Task<IReadOnlyList<VehicleCatalogRecord>> SearchAsync(VehicleSearchFilter filter, CancellationToken ct = default);
}
