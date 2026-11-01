namespace BonBon.Business.DTOs.Vehicles;

public sealed record VehicleSearchRequest
{
    public string? Location { get; init; }
    public DateTimeOffset? StartAt { get; init; }
    public DateTimeOffset? EndAt { get; init; }
    public string? RentalType { get; init; }
    public short? Seats { get; init; }
    public string? Transmission { get; init; }
    public string? FuelType { get; init; }
    public string? BodyType { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
}

public sealed record VehicleOwnerSummary(
    Guid OwnerId,
    string FullName,
    string Email,
    string? Phone);

public sealed record VehicleCatalogItem(
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
    VehicleOwnerSummary Owner,
    string[] ImageUrls,
    string? ImageUrl,
    bool WeeklyDiscountEnabled,
    decimal? WeeklyDiscountPercent,
    bool MileageLimitEnabled,
    int? DailyMileageLimitKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    int? BillableUnits,
    decimal? QuotedRentalAmount,
    string? UnitLabel);
