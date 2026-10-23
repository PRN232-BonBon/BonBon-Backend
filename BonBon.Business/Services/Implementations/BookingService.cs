using BonBon.Business.Common;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class BookingService(IBookingRepository bookingRepository) : IBookingService
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private readonly IBookingRepository _bookingRepository = bookingRepository;

    public Task<ServiceResult<BookingDecisionResponse>> ApproveAsync(Guid ownerAccountId, Guid bookingId, CancellationToken ct = default) =>
        DecideAsync(ownerAccountId, bookingId, BookingDecision.Approve, ct);

    public Task<ServiceResult<BookingDecisionResponse>> RejectAsync(Guid ownerAccountId, Guid bookingId, CancellationToken ct = default) =>
        DecideAsync(ownerAccountId, bookingId, BookingDecision.Reject, ct);

    private async Task<ServiceResult<BookingDecisionResponse>> DecideAsync(
        Guid ownerAccountId, Guid bookingId, BookingDecision decision, CancellationToken ct)
    {
        if (ownerAccountId == Guid.Empty)
            return ServiceResult<BookingDecisionResponse>.Failure("OWNER_INACTIVE", "Tài khoản chủ xe không hợp lệ.");
        if (bookingId == Guid.Empty)
            return ServiceResult<BookingDecisionResponse>.Failure("BOOKING_ID_INVALID", "Mã đặt xe không hợp lệ.");
        try
        {
            var result = await _bookingRepository.DecideAsync(ownerAccountId, bookingId, decision, ct);
            if (result.Outcome is BookingDecisionOutcome.Applied or BookingDecisionOutcome.AlreadyApplied)
            {
                var item = result.Booking ?? throw new InvalidOperationException("Missing booking decision snapshot.");
                return ServiceResult<BookingDecisionResponse>.Success(new(item.BookingId, item.Status,
                    item.UnitPrice, item.QuotedRentalAmount, item.Currency,
                    result.Outcome == BookingDecisionOutcome.AlreadyApplied));
            }
            var (code, message) = result.Outcome switch
            {
                BookingDecisionOutcome.NotFound => ("BOOKING_NOT_FOUND", "Không tìm thấy đơn đặt xe."),
                BookingDecisionOutcome.NotOwner => ("BOOKING_NOT_OWNER", "Bạn không phải chủ xe của đơn đặt này."),
                BookingDecisionOutcome.OwnerInactive => ("OWNER_INACTIVE", "Tài khoản và vai trò chủ xe phải đang hoạt động."),
                BookingDecisionOutcome.CustomerInactive => ("CUSTOMER_INACTIVE", "Tài khoản và vai trò khách hàng không còn hoạt động."),
                BookingDecisionOutcome.VehicleUnavailable => ("VEHICLE_UNAVAILABLE", "Xe hiện không sẵn sàng để duyệt đặt xe."),
                BookingDecisionOutcome.ScheduleConflict => ("SCHEDULE_CONFLICT", "Xe đã có lịch trong khoảng thời gian này."),
                BookingDecisionOutcome.BookingWindowPassed => ("BOOKING_WINDOW_PASSED", "Đã qua thời gian nhận xe; không thể duyệt."),
                BookingDecisionOutcome.StateConflict => ("BOOKING_STATE_CONFLICT", "Đơn đặt xe đã có quyết định hoặc đã chuyển sang bước khác."),
                BookingDecisionOutcome.ConcurrentChange => ("BOOKING_DECISION_RETRY", "Đơn đặt xe vừa thay đổi. Vui lòng thử lại."),
                _ => throw new InvalidOperationException("Unknown booking decision outcome."),
            };
            return ServiceResult<BookingDecisionResponse>.Failure(code, message);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return ServiceResult<BookingDecisionResponse>.Failure("SCHEDULE_CONFLICT", "Xe vừa được giữ lịch cho một đơn đặt khác.");
        }
        catch (PostgresException exception) when (exception.SqlState is "40001" or "40P01" or "55P03" or "57014")
        {
            return ServiceResult<BookingDecisionResponse>.Failure("BOOKING_DECISION_RETRY", "Dữ liệu đang được xử lý. Vui lòng thử lại.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return ServiceResult<BookingDecisionResponse>.Failure("BOOKING_DECISION_REJECTED", "Đơn đặt xe không đáp ứng điều kiện duyệt hoặc từ chối.");
        }
    }

    public ServiceResult<RentalWindowResponse> ValidateWindow(RentalWindowRequest request) =>
        ValidateWindowCore(request.RentalType, request.StartAt, request.EndAt);

    public async Task<ServiceResult<BookingQuoteResponse>> QuoteAsync(
        BookingQuoteRequest request,
        CancellationToken ct = default)
    {
        if (request.VehicleId == Guid.Empty)
            return ServiceResult<BookingQuoteResponse>.Failure("VEHICLE_REQUIRED", "Vui lòng chọn xe cần thuê.");

        var window = ValidateWindowCore(request.RentalType, request.StartAt, request.EndAt);
        if (!window.IsSuccess)
            return ServiceResult<BookingQuoteResponse>.Failure(window.ErrorCode!, window.ErrorMessage!);

        var vehicle = await _bookingRepository.GetVehiclePricingAsync(request.VehicleId, ct);
        if (vehicle is null)
            return ServiceResult<BookingQuoteResponse>.Failure("VEHICLE_NOT_FOUND", "Không tìm thấy xe.");
        if (vehicle.Status != "Available")
            return ServiceResult<BookingQuoteResponse>.Failure("VEHICLE_UNAVAILABLE", "Xe hiện chưa sẵn sàng để đặt.");

        var normalized = window.Data!;
        var unitPrice = normalized.RentalType == "Hourly" ? vehicle.HourPrice : vehicle.DayPrice;
        var available = await _bookingRepository.IsVehicleAvailableAsync(
            request.VehicleId, normalized.StartAt, normalized.EndAt, ct);

        return ServiceResult<BookingQuoteResponse>.Success(new BookingQuoteResponse(
            request.VehicleId,
            normalized.RentalType,
            normalized.StartAt,
            normalized.EndAt,
            normalized.BillableUnits,
            unitPrice,
            unitPrice * normalized.BillableUnits,
            "VND",
            available));
    }

    public async Task<ServiceResult<bool>> IsAvailableAsync(
        Guid vehicleId,
        RentalWindowRequest request,
        CancellationToken ct = default)
    {
        if (vehicleId == Guid.Empty)
            return ServiceResult<bool>.Failure("VEHICLE_REQUIRED", "Vui lòng chọn xe cần kiểm tra.");

        var window = ValidateWindow(request);
        if (!window.IsSuccess)
            return ServiceResult<bool>.Failure(window.ErrorCode!, window.ErrorMessage!);

        var available = await _bookingRepository.IsVehicleAvailableAsync(
            vehicleId, window.Data!.StartAt, window.Data.EndAt, ct);
        return ServiceResult<bool>.Success(available);
    }

    public async Task<ServiceResult<BookingResponse>> CreateAsync(
        Guid customerAccountId,
        CreateBookingRequest request,
        CancellationToken ct = default)
    {
        var quote = await QuoteAsync(new BookingQuoteRequest
        {
            VehicleId = request.VehicleId,
            RentalType = request.RentalType,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
        }, ct);

        if (!quote.IsSuccess)
            return ServiceResult<BookingResponse>.Failure(quote.ErrorCode!, quote.ErrorMessage!);
        if (!quote.Data!.IsAvailable)
            return ServiceResult<BookingResponse>.Failure("SCHEDULE_CONFLICT", "Xe đã có lịch trong khoảng thời gian này.");

        try
        {
            var created = await _bookingRepository.CreateAsync(
                customerAccountId,
                request.VehicleId,
                quote.Data.RentalType,
                quote.Data.StartAt,
                quote.Data.EndAt,
                ct);

            return ServiceResult<BookingResponse>.Success(new BookingResponse(
                created.BookingId,
                created.VehicleId,
                quote.Data.RentalType,
                quote.Data.StartAt,
                quote.Data.EndAt,
                created.BillableUnits,
                created.UnitPrice,
                created.QuotedRentalAmount,
                created.Currency,
                created.Status));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return ServiceResult<BookingResponse>.Failure("SCHEDULE_CONFLICT", "Xe vừa được đặt trong khoảng thời gian này.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return ServiceResult<BookingResponse>.Failure("BOOKING_REJECTED", exception.MessageText);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<CustomerBookingResponse>>> GetMineAsync(
        Guid customerAccountId,
        CancellationToken ct = default)
    {
        var bookings = await _bookingRepository.GetByCustomerAsync(customerAccountId, ct);
        IReadOnlyList<CustomerBookingResponse> response = bookings.Select(item => new CustomerBookingResponse(
            item.BookingId,
            $"{item.Brand} {item.Model}",
            item.PlateNumber,
            item.CoverImageUrl,
            item.RentalType,
            item.StartAt,
            item.EndAt,
            item.QuotedRentalAmount,
            item.Currency,
            item.Status,
            item.CreatedAt)).ToList();
        return ServiceResult<IReadOnlyList<CustomerBookingResponse>>.Success(response);
    }

    private static ServiceResult<RentalWindowResponse> ValidateWindowCore(
        string rentalType,
        DateTimeOffset startAt,
        DateTimeOffset endAt)
    {
        var normalizedType = rentalType?.Trim().ToLowerInvariant() switch
        {
            "hourly" => "Hourly",
            "daily" => "Daily",
            _ => null,
        };

        if (normalizedType is null)
            return ServiceResult<RentalWindowResponse>.Failure("INVALID_RENTAL_TYPE", "Loại hình thuê không hợp lệ.");
        if (startAt == default || endAt == default || endAt <= startAt)
            return ServiceResult<RentalWindowResponse>.Failure("INVALID_INTERVAL", "Thời gian trả xe phải sau thời gian nhận xe.");

        var duration = endAt - startAt;
        int units;
        string unitLabel;

        if (normalizedType == "Hourly")
        {
            var localStart = startAt.ToOffset(VietnamOffset);
            var wholeHours = duration.TotalMinutes / 60;
            if (localStart.Second != 0 || localStart.Minute is not (0 or 30))
                return ServiceResult<RentalWindowResponse>.Failure("INVALID_TIME_SLOT", "Giờ nhận xe phải theo bước 30 phút.");
            if (wholeHours < 4 || wholeHours > 8 || wholeHours != Math.Truncate(wholeHours))
                return ServiceResult<RentalWindowResponse>.Failure("INVALID_HOURLY_DURATION", "Thuê theo giờ áp dụng từ 4 đến 8 giờ nguyên.");

            units = (int)wholeHours;
            unitLabel = "giờ";
        }
        else
        {
            var localStart = startAt.ToOffset(VietnamOffset);
            var localEnd = endAt.ToOffset(VietnamOffset);
            if (localEnd.Date <= localStart.Date)
                return ServiceResult<RentalWindowResponse>.Failure("INVALID_DAILY_INTERVAL", "Thuê theo ngày cần trả xe vào một ngày sau ngày nhận.");
            if (duration > TimeSpan.FromDays(30))
                return ServiceResult<RentalWindowResponse>.Failure("DAILY_LIMIT_EXCEEDED", "Mỗi lượt thuê xe được giới hạn tối đa 30 ngày.");

            units = (int)Math.Ceiling(duration.TotalHours / 24d);
            unitLabel = "ngày";
        }

        return ServiceResult<RentalWindowResponse>.Success(new RentalWindowResponse(
            normalizedType,
            startAt.ToUniversalTime(),
            endAt.ToUniversalTime(),
            units,
            unitLabel));
    }
}
