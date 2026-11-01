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
        if (!vehicle.IsOwnerActive)
            return ServiceResult<BookingQuoteResponse>.Failure("OWNER_INACTIVE", "Chủ xe hiện chưa sẵn sàng cho thuê.");

        var normalized = window.Data!;
        var unitPrice = normalized.RentalType == "Hourly" ? vehicle.HourPrice : vehicle.DayPrice;
        var baseRentalAmount = unitPrice * normalized.BillableUnits;
        var discountPercent = normalized.RentalType == "Daily"
            && normalized.BillableUnits >= 7
            && vehicle.WeeklyDiscountEnabled
            ? vehicle.WeeklyDiscountPercent ?? 0
            : 0;
        var discountAmount = decimal.Round(baseRentalAmount * discountPercent / 100, 0, MidpointRounding.AwayFromZero);
        var includedMileageKm = vehicle.MileageLimitEnabled
            ? vehicle.DailyMileageLimitKm * (normalized.RentalType == "Daily" ? normalized.BillableUnits : 1)
            : null;
        var available = await _bookingRepository.IsVehicleAvailableAsync(
            request.VehicleId, normalized.StartAt, normalized.EndAt, ct);

        return ServiceResult<BookingQuoteResponse>.Success(new BookingQuoteResponse(
            request.VehicleId,
            normalized.RentalType,
            normalized.StartAt,
            normalized.EndAt,
            normalized.BillableUnits,
            unitPrice,
            baseRentalAmount,
            discountPercent,
            discountAmount,
            baseRentalAmount - discountAmount,
            includedMileageKm,
            vehicle.MileageLimitEnabled ? vehicle.OverageFeePerKm : null,
            vehicle.RentalTerms,
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

        var vehicle = await _bookingRepository.GetVehiclePricingAsync(request.VehicleId, ct);
        if (vehicle is null)
            return ServiceResult<BookingResponse>.Failure("VEHICLE_NOT_FOUND", "Không tìm thấy xe.");
        if (vehicle.Status != "Available")
            return ServiceResult<BookingResponse>.Failure("VEHICLE_UNAVAILABLE", "Xe hiện chưa sẵn sàng để đặt.");
        if (!vehicle.IsOwnerActive)
            return ServiceResult<BookingResponse>.Failure("OWNER_INACTIVE", "Chủ xe hiện không còn hoạt động.");

        // BE-01: Chặn tự thuê xe của mình
        if (vehicle.OwnerAccountId == customerAccountId)
            return ServiceResult<BookingResponse>.Failure("SELF_RENTAL_NOT_ALLOWED", "Bạn không thể tự thuê xe của chính mình.");

        // BE-01: Chống tạo đơn trùng khi gửi lại request
        var isDuplicate = await _bookingRepository.HasDuplicatePendingBookingAsync(
            customerAccountId, request.VehicleId, quote.Data.StartAt, quote.Data.EndAt, ct);
        if (isDuplicate)
            return ServiceResult<BookingResponse>.Failure("DUPLICATE_BOOKING", "Bạn đã có một yêu cầu đặt xe đang chờ xử lý cho xe và khoảng thời gian này.");

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
                created.UnitPrice * created.BillableUnits,
                created.WeeklyDiscountPercent,
                created.DiscountAmount,
                created.QuotedRentalAmount,
                created.IncludedMileageKm,
                created.OverageFeePerKm,
                created.RentalTerms,
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

    public async Task<ServiceResult<BookingDetailResponse>> GetDetailAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin = false,
        CancellationToken ct = default)
    {
        var item = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (item is null)
            return ServiceResult<BookingDetailResponse>.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && item.CustomerAccountId != currentAccountId && item.OwnerAccountId != currentAccountId)
            return ServiceResult<BookingDetailResponse>.Failure("FORBIDDEN", "Bạn không có quyền xem thông tin đơn đặt xe này.");

        var inspections = item.Inspections.Select(i => new InspectionResponse(
            i.InspectionId, i.Kind, i.OdometerKm, i.FuelLevel, i.ConditionNotes, i.InspectedAt)).ToList();

        var charges = item.AdditionalCharges.Select(c => new AdditionalChargeResponse(
            c.AdditionalChargeId, c.ReasonCode, c.Description, c.Amount, c.Currency, c.Status, c.ProposedAt)).ToList();

        var response = new BookingDetailResponse(
            item.BookingId,
            item.Status,
            item.RentalType,
            item.StartAt,
            item.EndAt,
            item.BillableUnits,
            item.UnitPrice,
            item.UnitPrice * item.BillableUnits,
            item.WeeklyDiscountPercent,
            item.DiscountAmount,
            item.QuotedRentalAmount,
            item.IncludedMileageKm,
            item.OverageFeePerKm,
            item.RentalTerms,
            item.Currency,
            item.CreatedAt,
            new BookingVehicleInfo(
                item.VehicleId,
                item.Brand,
                item.Model,
                item.PlateNumber,
                item.CoverImageUrl,
                item.PickupAddress,
                item.Seats,
                item.Transmission,
                item.FuelType),
            new BookingCustomerInfo(
                item.CustomerAccountId,
                item.CustomerName,
                item.CustomerEmail,
                item.CustomerPhone),
            new BookingOwnerInfo(
                item.OwnerAccountId,
                item.OwnerName,
                item.OwnerEmail,
                item.OwnerPhone),
            inspections,
            charges);

        return ServiceResult<BookingDetailResponse>.Success(response);
    }

    public async Task<ServiceResult<IReadOnlyList<OwnerBookingResponse>>> GetOwnerBookingsAsync(
        Guid ownerAccountId,
        CancellationToken ct = default)
    {
        var bookings = await _bookingRepository.GetByOwnerAsync(ownerAccountId, ct);
        IReadOnlyList<OwnerBookingResponse> response = bookings.Select(item => new OwnerBookingResponse(
            item.BookingId,
            item.VehicleId,
            $"{item.Brand} {item.Model}",
            item.PlateNumber,
            item.CoverImageUrl,
            item.CustomerName,
            item.CustomerPhone,
            item.RentalType,
            item.StartAt,
            item.EndAt,
            item.QuotedRentalAmount,
            item.Currency,
            item.Status,
            item.CreatedAt)).ToList();

        return ServiceResult<IReadOnlyList<OwnerBookingResponse>>.Success(response);
    }

    public async Task<ServiceResult> CancelAsync(
        Guid bookingId,
        Guid customerAccountId,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (booking.CustomerAccountId != customerAccountId)
            return ServiceResult.Failure("FORBIDDEN", "Chỉ khách hàng tạo đơn mới có quyền hủy đơn đặt xe này.");

        if (booking.Status != "PendingApproval" && booking.Status != "AwaitingPayment")
            return ServiceResult.Failure("INVALID_STATUS", "Chỉ có thể hủy đơn khi đang chờ duyệt hoặc chờ thanh toán.");

        var success = await _bookingRepository.CancelAsync(bookingId, ct);
        if (!success)
            return ServiceResult.Failure("CANCEL_FAILED", "Không thể hủy đơn đặt xe lúc này. Vui lòng thử lại.");

        return ServiceResult.Success();
    }

    public async Task<ServiceResult> ApproveAsync(
        Guid bookingId,
        Guid ownerAccountId,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (booking.OwnerAccountId != ownerAccountId)
            return ServiceResult.Failure("FORBIDDEN", "Chỉ chủ xe mới có quyền duyệt đơn đặt xe này.");

        if (booking.Status != "PendingApproval")
            return ServiceResult.Failure("INVALID_STATUS", "Đơn đặt xe không ở trạng thái chờ duyệt.");

        // BE-01: Kiểm tra thời gian không được trôi vào quá khứ
        if (booking.StartAt < DateTimeOffset.UtcNow.AddMinutes(-2))
            return ServiceResult.Failure("PAST_START_TIME", "Thời gian nhận xe đã trôi qua, không thể duyệt đơn.");

        // BE-01: Kiểm tra lại trạng thái xe
        var vehicle = await _bookingRepository.GetVehiclePricingAsync(booking.VehicleId, ct);
        if (vehicle is null || vehicle.Status != "Available" || !vehicle.IsOwnerActive)
            return ServiceResult.Failure("VEHICLE_UNAVAILABLE", "Xe hiện không sẵn sàng để duyệt cho thuê.");

        // BE-01: Kiểm tra lịch trống
        var isAvailable = await _bookingRepository.IsVehicleAvailableAsync(booking.VehicleId, booking.StartAt, booking.EndAt, ct);
        if (!isAvailable)
            return ServiceResult.Failure("SCHEDULE_CONFLICT", "Xe đã có lịch trong khoảng thời gian này.");

        try
        {
            var success = await _bookingRepository.ApproveAsync(bookingId, ct);
            if (!success)
                return ServiceResult.Failure("APPROVE_FAILED", "Không thể duyệt đơn đặt xe lúc này.");

            return ServiceResult.Success();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            return ServiceResult.Failure("SCHEDULE_CONFLICT", "Xe đã có lịch được xác nhận trong khoảng thời gian này.");
        }
    }

    public async Task<ServiceResult> RejectAsync(
        Guid bookingId,
        Guid ownerAccountId,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (booking.OwnerAccountId != ownerAccountId)
            return ServiceResult.Failure("FORBIDDEN", "Chỉ chủ xe mới có quyền từ chối đơn đặt xe này.");

        if (booking.Status != "PendingApproval")
            return ServiceResult.Failure("INVALID_STATUS", "Đơn đặt xe không ở trạng thái chờ duyệt.");

        var success = await _bookingRepository.RejectAsync(bookingId, ct);
        if (!success)
            return ServiceResult.Failure("REJECT_FAILED", "Không thể từ chối đơn đặt xe lúc này.");

        return ServiceResult.Success();
    }

    public async Task<ServiceResult> ConfirmPaymentAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin = false,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && booking.CustomerAccountId != currentAccountId && booking.OwnerAccountId != currentAccountId)
            return ServiceResult.Failure("FORBIDDEN", "Bạn không có quyền xác nhận thanh toán cho đơn này.");

        if (booking.Status != "AwaitingPayment")
            return ServiceResult.Failure("INVALID_STATUS", "Đơn đặt xe không ở trạng thái chờ thanh toán.");

        var success = await _bookingRepository.ConfirmPaymentAsync(bookingId, ct);
        if (!success)
            return ServiceResult.Failure("CONFIRM_PAYMENT_FAILED", "Không thể xác nhận thanh toán lúc này.");

        return ServiceResult.Success();
    }

    public async Task<ServiceResult<InspectionResponse>> ConfirmPickupAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin,
        ConfirmPickupRequest request,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<InspectionResponse>.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && booking.CustomerAccountId != currentAccountId && booking.OwnerAccountId != currentAccountId)
            return ServiceResult<InspectionResponse>.Failure("FORBIDDEN", "Bạn không có quyền thực hiện nhận xe cho đơn này.");

        // BE-05: Kiểm tra thứ tự chuyển trạng thái — không nhận xe khi chưa thanh toán (Confirmed)
        if (booking.Status is "PendingApproval" or "AwaitingPayment")
            return ServiceResult<InspectionResponse>.Failure("PAYMENT_REQUIRED", "Không thể nhận xe khi chưa hoàn tất thanh toán / xác nhận.");

        if (booking.Status is not ("Confirmed" or "ReadyForPickup"))
            return ServiceResult<InspectionResponse>.Failure("INVALID_STATUS", "Đơn đặt xe không ở trạng thái chờ nhận xe.");

        if (request.OdometerKm < 0)
            return ServiceResult<InspectionResponse>.Failure("INVALID_ODOMETER", "Số km ban đầu phải lớn hơn hoặc bằng 0.");

        if (request.FuelLevel is < 0 or > 100)
            return ServiceResult<InspectionResponse>.Failure("INVALID_FUEL_LEVEL", "Mức xăng phải từ 0% đến 100%.");

        var inspection = await _bookingRepository.CreateInspectionAsync(
            bookingId,
            booking.VehicleId,
            "Pickup",
            request.OdometerKm,
            request.FuelLevel,
            request.ConditionNotes,
            request.InspectedAt,
            ct);

        await _bookingRepository.UpdateStatusAsync(bookingId, "InProgress", ["Confirmed", "ReadyForPickup"], ct);

        return ServiceResult<InspectionResponse>.Success(new InspectionResponse(
            inspection.InspectionId,
            inspection.Kind,
            inspection.OdometerKm,
            inspection.FuelLevel,
            inspection.ConditionNotes,
            inspection.InspectedAt));
    }

    public async Task<ServiceResult<ExcessMileageCalculationResponse>> ConfirmReturnAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin,
        ConfirmReturnRequest request,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && booking.CustomerAccountId != currentAccountId && booking.OwnerAccountId != currentAccountId)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("FORBIDDEN", "Bạn không có quyền thực hiện trả xe cho đơn này.");

        // BE-05: Chỉ trả xe khi InProgress
        if (booking.Status != "InProgress")
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("INVALID_STATUS", "Chỉ có thể trả xe khi chuyến đi đang diễn ra (InProgress).");

        var pickup = await _bookingRepository.GetInspectionAsync(bookingId, "Pickup", ct);
        if (pickup is null)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("MISSING_PICKUP_INSPECTION", "Không tìm thấy biên bản nhận xe ban đầu.");

        if (request.OdometerKm < (pickup.OdometerKm ?? 0))
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("INVALID_ODOMETER", "Số km lúc trả không được nhỏ hơn số km lúc nhận xe.");

        if (request.FuelLevel is < 0 or > 100)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("INVALID_FUEL_LEVEL", "Mức xăng phải từ 0% đến 100%.");

        var returnInspection = await _bookingRepository.CreateInspectionAsync(
            bookingId,
            booking.VehicleId,
            "Return",
            request.OdometerKm,
            request.FuelLevel,
            request.ConditionNotes,
            request.InspectedAt,
            ct);

        // BE-06: Tính quãng đường thực tế và phí vượt km theo snapshot đã lưu lúc đặt
        var actualDistance = request.OdometerKm - (pickup.OdometerKm ?? 0);
        decimal excessKm = 0;
        decimal excessFee = 0;
        Guid? chargeId = null;

        if (booking.IncludedMileageKm.HasValue && booking.IncludedMileageKm.Value > 0 && actualDistance > booking.IncludedMileageKm.Value)
        {
            excessKm = actualDistance - booking.IncludedMileageKm.Value;
            var feeRate = booking.OverageFeePerKm ?? 0;
            excessFee = Math.Round(excessKm * feeRate, 0, MidpointRounding.AwayFromZero);

            if (excessFee > 0)
            {
                var charge = await _bookingRepository.SaveExcessMileageChargeAsync(
                    bookingId, excessKm, feeRate, excessFee, ct);
                chargeId = charge?.AdditionalChargeId;
            }
        }

        await _bookingRepository.UpdateStatusAsync(bookingId, "Returned", ["InProgress"], ct);

        return ServiceResult<ExcessMileageCalculationResponse>.Success(new ExcessMileageCalculationResponse(
            actualDistance,
            booking.IncludedMileageKm,
            excessKm,
            booking.OverageFeePerKm,
            excessFee,
            chargeId));
    }

    public async Task<ServiceResult> CompleteBookingAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin = false,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && booking.CustomerAccountId != currentAccountId && booking.OwnerAccountId != currentAccountId)
            return ServiceResult.Failure("FORBIDDEN", "Bạn không có quyền hoàn thành đơn đặt xe này.");

        // BE-05: Không hoàn thành khi chưa trả xe
        if (booking.Status is not ("Returned" or "AwaitingSettlement"))
            return ServiceResult.Failure("INVALID_STATUS", "Không thể hoàn thành chuyến đi khi xe chưa được trả.");

        var success = await _bookingRepository.UpdateStatusAsync(bookingId, "Completed", ["Returned", "AwaitingSettlement"], ct);
        if (!success)
            return ServiceResult.Failure("COMPLETE_FAILED", "Không thể hoàn thành chuyến đi lúc này.");

        return ServiceResult.Success();
    }

    public async Task<ServiceResult<ExcessMileageCalculationResponse>> CalculateExcessMileageAsync(
        Guid bookingId,
        Guid currentAccountId,
        bool isAdmin = false,
        CancellationToken ct = default)
    {
        var booking = await _bookingRepository.GetByIdAsync(bookingId, ct);
        if (booking is null)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("BOOKING_NOT_FOUND", "Không tìm thấy thông tin đơn đặt xe.");

        if (!isAdmin && booking.CustomerAccountId != currentAccountId && booking.OwnerAccountId != currentAccountId)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("FORBIDDEN", "Bạn không có quyền truy cập thông tin phí của đơn này.");

        var pickup = await _bookingRepository.GetInspectionAsync(bookingId, "Pickup", ct);
        var returnIns = await _bookingRepository.GetInspectionAsync(bookingId, "Return", ct);

        if (pickup is null || returnIns is null)
            return ServiceResult<ExcessMileageCalculationResponse>.Failure("INSPECTIONS_INCOMPLETE", "Chưa đủ biên bản nhận và trả xe để tính phí.");

        var actualDistance = (returnIns.OdometerKm ?? 0) - (pickup.OdometerKm ?? 0);
        decimal excessKm = 0;
        decimal excessFee = 0;
        Guid? chargeId = null;

        if (booking.IncludedMileageKm.HasValue && booking.IncludedMileageKm.Value > 0 && actualDistance > booking.IncludedMileageKm.Value)
        {
            excessKm = actualDistance - booking.IncludedMileageKm.Value;
            var feeRate = booking.OverageFeePerKm ?? 0;
            excessFee = Math.Round(excessKm * feeRate, 0, MidpointRounding.AwayFromZero);

            if (excessFee > 0)
            {
                var charge = await _bookingRepository.SaveExcessMileageChargeAsync(
                    bookingId, excessKm, feeRate, excessFee, ct);
                chargeId = charge?.AdditionalChargeId;
            }
        }

        return ServiceResult<ExcessMileageCalculationResponse>.Success(new ExcessMileageCalculationResponse(
            actualDistance,
            booking.IncludedMileageKm,
            excessKm,
            booking.OverageFeePerKm,
            excessFee,
            chargeId));
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

        // BE-01: Chặn đặt trong quá khứ (cho phép 2 phút buffer cho độ lệch đồng hồ máy khách)
        if (startAt < DateTimeOffset.UtcNow.AddMinutes(-2))
            return ServiceResult<RentalWindowResponse>.Failure("PAST_START_TIME", "Thời gian nhận xe không được ở trong quá khứ.");

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
