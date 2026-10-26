namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record VehiclePricing(
    Guid VehicleId,
    decimal HourPrice,
    decimal DayPrice,
    string Status,
    bool WeeklyDiscountEnabled,
    decimal? WeeklyDiscountPercent,
    bool MileageLimitEnabled,
    int? DailyMileageLimitKm,
    decimal? OverageFeePerKm,
    string? RentalTerms);

public sealed record CreatedBooking(
    Guid BookingId,
    Guid VehicleId,
    int BillableUnits,
    decimal UnitPrice,
    decimal WeeklyDiscountPercent,
    decimal DiscountAmount,
    decimal QuotedRentalAmount,
    int? IncludedMileageKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    string Currency,
    string Status);

public sealed record CustomerBooking(
    Guid BookingId,
    string Brand,
    string Model,
    string PlateNumber,
    string? CoverImageUrl,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal QuotedRentalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record BookingDetailRecord(
    Guid BookingId,
    string Status,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int BillableUnits,
    decimal UnitPrice,
    decimal WeeklyDiscountPercent,
    decimal DiscountAmount,
    decimal QuotedRentalAmount,
    int? IncludedMileageKm,
    decimal? OverageFeePerKm,
    string? RentalTerms,
    string Currency,
    DateTimeOffset CreatedAt,
    // Vehicle
    Guid VehicleId,
    string Brand,
    string Model,
    string PlateNumber,
    string? CoverImageUrl,
    string PickupAddress,
    short Seats,
    string Transmission,
    string FuelType,
    // Customer
    Guid CustomerAccountId,
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    // Owner
    Guid OwnerAccountId,
    string OwnerName,
    string OwnerEmail,
    string? OwnerPhone);

public sealed record OwnerBookingRecord(
    Guid BookingId,
    Guid VehicleId,
    string Brand,
    string Model,
    string PlateNumber,
    string? CoverImageUrl,
    string CustomerName,
    string? CustomerPhone,
    string RentalType,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    decimal QuotedRentalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);

public interface IBookingRepository
{
    Task<VehiclePricing?> GetVehiclePricingAsync(Guid vehicleId, CancellationToken ct = default);
    Task<bool> IsVehicleAvailableAsync(Guid vehicleId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken ct = default);
    Task<CreatedBooking> CreateAsync(
        Guid customerAccountId,
        Guid vehicleId,
        string rentalType,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        CancellationToken ct = default);
    Task<IReadOnlyList<CustomerBooking>> GetByCustomerAsync(Guid customerAccountId, CancellationToken ct = default);
    Task<BookingDetailRecord?> GetByIdAsync(Guid bookingId, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerBookingRecord>> GetByOwnerAsync(Guid ownerAccountId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid bookingId, CancellationToken ct = default);
    Task<bool> ApproveAsync(Guid bookingId, CancellationToken ct = default);
    Task<bool> RejectAsync(Guid bookingId, CancellationToken ct = default);
}
