using BonBon.Business.Common;
using BonBon.Business.DTOs.Bookings;

namespace BonBon.Business.Services.Interfaces;

public interface IBookingService
{
    ServiceResult<RentalWindowResponse> ValidateWindow(RentalWindowRequest request);
    Task<ServiceResult<BookingQuoteResponse>> QuoteAsync(BookingQuoteRequest request, CancellationToken ct = default);
    Task<ServiceResult<bool>> IsAvailableAsync(Guid vehicleId, RentalWindowRequest request, CancellationToken ct = default);
    Task<ServiceResult<BookingResponse>> CreateAsync(Guid customerAccountId, CreateBookingRequest request, CancellationToken ct = default);
    Task<ServiceResult<IReadOnlyList<CustomerBookingResponse>>> GetMineAsync(Guid customerAccountId, CancellationToken ct = default);
}
