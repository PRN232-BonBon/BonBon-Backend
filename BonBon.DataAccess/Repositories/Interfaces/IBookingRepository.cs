namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record VehiclePricing(Guid VehicleId, decimal HourPrice, decimal DayPrice, string Status);

public sealed record CreatedBooking(
    Guid BookingId,
    Guid VehicleId,
    int BillableUnits,
    decimal UnitPrice,
    decimal QuotedRentalAmount,
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
}
