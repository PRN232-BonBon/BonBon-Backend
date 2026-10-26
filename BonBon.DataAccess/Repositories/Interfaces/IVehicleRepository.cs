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

public interface IVehicleRepository
{
    Task<RegisteredVehicle> RegisterAsync(
        Guid ownerAccountId,
        VehicleRegistrationSubmission submission,
        CancellationToken ct = default);

    Task<IReadOnlyList<OwnerVehicle>> GetMineAsync(Guid ownerAccountId, CancellationToken ct = default);
    Task<(byte[] Data, string ContentType)?> GetImageAsync(Guid imageId, CancellationToken ct = default);
}
