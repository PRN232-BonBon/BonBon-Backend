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
    Task<ServiceResult<BookingDetailResponse>> GetDetailAsync(Guid bookingId, Guid currentAccountId, bool isAdmin = false, CancellationToken ct = default);
    Task<ServiceResult<IReadOnlyList<OwnerBookingResponse>>> GetOwnerBookingsAsync(Guid ownerAccountId, CancellationToken ct = default);
    Task<ServiceResult> CancelAsync(Guid bookingId, Guid customerAccountId, CancellationToken ct = default);
    Task<ServiceResult> ApproveAsync(Guid bookingId, Guid ownerAccountId, CancellationToken ct = default);
    Task<ServiceResult> RejectAsync(Guid bookingId, Guid ownerAccountId, CancellationToken ct = default);
    Task<ServiceResult> ConfirmPaymentAsync(Guid bookingId, Guid currentAccountId, bool isAdmin = false, CancellationToken ct = default);
    Task<ServiceResult<InspectionResponse>> ConfirmPickupAsync(Guid bookingId, Guid currentAccountId, bool isAdmin, ConfirmPickupRequest request, CancellationToken ct = default);
    Task<ServiceResult<ExcessMileageCalculationResponse>> ConfirmReturnAsync(Guid bookingId, Guid currentAccountId, bool isAdmin, ConfirmReturnRequest request, CancellationToken ct = default);
    Task<ServiceResult> CompleteBookingAsync(Guid bookingId, Guid currentAccountId, bool isAdmin = false, CancellationToken ct = default);
    Task<ServiceResult<ExcessMileageCalculationResponse>> CalculateExcessMileageAsync(Guid bookingId, Guid currentAccountId, bool isAdmin = false, CancellationToken ct = default);
}
