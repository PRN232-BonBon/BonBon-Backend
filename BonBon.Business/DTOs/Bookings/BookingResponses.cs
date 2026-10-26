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

public sealed record BookingVehicleInfo(
    Guid VehicleId,
    string Brand,
    string Model,
    string PlateNumber,
    string? CoverImageUrl,
    string PickupAddress,
    short Seats,
    string Transmission,
    string FuelType);

public sealed record BookingCustomerInfo(
    Guid CustomerId,
    string FullName,
    string Email,
    string? Phone);

public sealed record BookingOwnerInfo(
    Guid OwnerId,
    string FullName,
    string Email,
    string? Phone);

public sealed record BookingDetailResponse(
    Guid BookingId,
    string Status,
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
    DateTimeOffset CreatedAt,
    BookingVehicleInfo Vehicle,
    BookingCustomerInfo Customer,
    BookingOwnerInfo Owner);

public sealed record OwnerBookingResponse(
    Guid BookingId,
    Guid VehicleId,
    string VehicleName,
    string PlateNumber,
    string? CoverImageUrl,
    string CustomerName,
    string? CustomerPhone,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);
