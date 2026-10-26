namespace BonBon.Business.DTOs.Bookings;

public sealed record RentalWindowResponse(
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int BillableUnits,
    string UnitLabel);

public sealed record BookingQuoteResponse(
    Guid VehicleId,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int BillableUnits,
    decimal UnitPrice,
    decimal BaseRentalAmount,
    decimal WeeklyDiscountPercent,
    decimal DiscountAmount,
    decimal QuotedRentalAmount,
    int? IncludedMileageKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    string Currency,
    bool IsAvailable);

public sealed record BookingResponse(
    Guid BookingId,
    Guid VehicleId,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int BillableUnits,
    decimal UnitPrice,
    decimal BaseRentalAmount,
    decimal WeeklyDiscountPercent,
    decimal DiscountAmount,
    decimal QuotedRentalAmount,
    int? IncludedMileageKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    string Currency,
    string Status);

public sealed record CustomerBookingResponse(
    Guid BookingId,
    string VehicleName,
    string PlateNumber,
    string? CoverImageUrl,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);
