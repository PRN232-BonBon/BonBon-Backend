using System.ComponentModel.DataAnnotations;

namespace BonBon.Business.DTOs.Vehicles;

public sealed record RegisterVehicleRequest
{
    [Required, MaxLength(30)] public string PlateNumber { get; init; } = default!;
    [Required, MaxLength(80)] public string Brand { get; init; } = default!;
    [Required, MaxLength(100)] public string Model { get; init; } = default!;
    [Range(1990, 2100)] public short ProductionYear { get; init; }
    [Range(4, 7)] public short Seats { get; init; }
    [Required] public string Transmission { get; init; } = default!;
    [Required] public string FuelType { get; init; } = default!;
    [Required] public string BodyType { get; init; } = default!;
    [Required, MinLength(20), MaxLength(2000)] public string Description { get; init; } = default!;
    [Required, MinLength(10), MaxLength(1000)] public string PickupAddress { get; init; } = default!;
    [Range(50000, 10000000)] public decimal HourPrice { get; init; }
    [Range(300000, 100000000)] public decimal DayPrice { get; init; }
    public bool WeeklyDiscountEnabled { get; init; }
    public decimal? WeeklyDiscountPercent { get; init; }
    public bool MileageLimitEnabled { get; init; } = true;
    public int? DailyMileageLimitKm { get; init; }
    public decimal? OverageFeePerKm { get; init; }
    [MaxLength(1000)] public string? RentalTerms { get; init; }
    public string[] Features { get; init; } = [];
}

public sealed record VehicleRegistrationResponse(Guid VehicleId, string Status, int ImageCount);

public sealed record OwnerVehicleResponse(
    Guid VehicleId,
    string Brand,
    string Model,
    string PlateNumber,
    string Status,
    string? RejectionReason,
    int ImageCount);
