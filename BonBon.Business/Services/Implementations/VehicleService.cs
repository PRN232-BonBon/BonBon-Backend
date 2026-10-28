using BonBon.Business.Common;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.DTOs.Vehicles;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class VehicleService(
    IVehicleRepository vehicleRepository,
    IBookingService bookingService) : IVehicleService
{
    private static readonly HashSet<string> Transmissions = ["Manual", "Automatic"];
    private static readonly HashSet<string> FuelTypes = ["Gasoline", "Diesel", "Electric", "Hybrid"];
    private static readonly HashSet<string> BodyTypes = ["Sedan", "SUV", "MPV", "Hatchback"];
    private readonly IVehicleRepository _vehicleRepository = vehicleRepository;
    private readonly IBookingService _bookingService = bookingService;

    public async Task<ServiceResult<VehicleRegistrationResponse>> RegisterAsync(
        Guid ownerAccountId,
        RegisterVehicleRequest request,
        IReadOnlyList<StoredImage> vehicleImages,
        CancellationToken ct = default)
    {
        if (vehicleImages.Count is < 1 or > 8)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_IMAGE_COUNT", "Vui lòng tải từ 1 đến 8 ảnh xe.");
        if (string.IsNullOrWhiteSpace(request.PlateNumber) || request.PlateNumber.Length > 30
            || string.IsNullOrWhiteSpace(request.Brand) || request.Brand.Length > 80
            || string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 100)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_VEHICLE_INFO", "Thông tin nhận diện xe chưa hợp lệ.");
        if (request.ProductionYear is < 1990 or > 2100 || request.Seats is < 4 or > 7)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_VEHICLE_INFO", "Năm sản xuất hoặc số chỗ không hợp lệ.");
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length is < 20 or > 2000
            || string.IsNullOrWhiteSpace(request.PickupAddress) || request.PickupAddress.Trim().Length is < 10 or > 1000)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_VEHICLE_INFO", "Mô tả hoặc địa chỉ nhận xe chưa hợp lệ.");
        if (request.HourPrice is < 50000 or > 10000000 || request.DayPrice is < 300000 or > 100000000)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_PRICE", "Đơn giá thuê xe chưa hợp lệ.");
        if (!string.IsNullOrWhiteSpace(request.RentalTerms) && request.RentalTerms.Trim().Length is < 10 or > 1000)
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_RENTAL_TERMS", "Điều khoản thuê xe phải từ 10 đến 1.000 ký tự.");
        if (request.Features.Length > 20 || request.Features.Any(string.IsNullOrWhiteSpace))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_FEATURES", "Danh sách tiện nghi xe chưa hợp lệ.");
        if (!Transmissions.Contains(request.Transmission))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_TRANSMISSION", "Loại truyền động không hợp lệ.");
        if (!FuelTypes.Contains(request.FuelType))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_FUEL_TYPE", "Loại nhiên liệu không hợp lệ.");
        if (!BodyTypes.Contains(request.BodyType))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_BODY_TYPE", "Dòng xe không hợp lệ.");
        if (request.WeeklyDiscountEnabled && request.WeeklyDiscountPercent is not (>= 5 and <= 30))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_WEEKLY_DISCOUNT", "Mức giảm giá tuần phải từ 5% đến 30%.");
        if (request.MileageLimitEnabled &&
            (request.DailyMileageLimitKm is not (>= 100 and <= 800)
             || request.OverageFeePerKm is not (>= 1000 and <= 10000)))
            return ServiceResult<VehicleRegistrationResponse>.Failure("INVALID_MILEAGE_POLICY", "Giới hạn km hoặc phí vượt km không hợp lệ.");

        try
        {
            var response = await _vehicleRepository.RegisterAsync(ownerAccountId, new VehicleRegistrationSubmission(
                new VehicleRegistrationData(
                    request.PlateNumber.Trim(),
                    request.Brand.Trim(),
                    request.Model.Trim(),
                    request.ProductionYear,
                    request.Seats,
                    request.Transmission,
                    request.FuelType,
                    request.BodyType,
                    request.Description.Trim(),
                    request.PickupAddress.Trim(),
                    request.HourPrice,
                    request.DayPrice,
                    request.WeeklyDiscountEnabled,
                    request.WeeklyDiscountEnabled ? request.WeeklyDiscountPercent : null,
                    request.MileageLimitEnabled,
                    request.MileageLimitEnabled ? request.DailyMileageLimitKm : null,
                    request.MileageLimitEnabled ? request.OverageFeePerKm : null,
                    string.IsNullOrWhiteSpace(request.RentalTerms) ? null : request.RentalTerms.Trim(),
                    request.Features.Select(item => item.Trim()).Distinct().ToArray()),
                vehicleImages), ct);

            return ServiceResult<VehicleRegistrationResponse>.Success(
                new VehicleRegistrationResponse(response.VehicleId, response.Status, response.ImageCount));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return ServiceResult<VehicleRegistrationResponse>.Failure("PLATE_ALREADY_EXISTS", "Biển số xe này đã được đăng ký.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            return ServiceResult<VehicleRegistrationResponse>.Failure("VEHICLE_REJECTED", exception.MessageText);
        }
    }

    public async Task<ServiceResult<IReadOnlyList<OwnerVehicleResponse>>> GetMineAsync(
        Guid ownerAccountId,
        CancellationToken ct = default) =>
        ServiceResult<IReadOnlyList<OwnerVehicleResponse>>.Success(
            (await _vehicleRepository.GetMineAsync(ownerAccountId, ct))
                .Select(item => new OwnerVehicleResponse(
                    item.VehicleId, item.Brand, item.Model, item.PlateNumber,
                    item.Status, item.RejectionReason, item.ImageCount))
                .ToList());

    public async Task<ServiceResult<IReadOnlyList<VehicleCatalogItem>>> SearchAsync(
        VehicleSearchRequest request,
        CancellationToken ct = default)
    {
        RentalWindowResponse? window = null;
        if (request.StartAt.HasValue && request.EndAt.HasValue && !string.IsNullOrWhiteSpace(request.RentalType))
        {
            var windowResult = _bookingService.ValidateWindow(new RentalWindowRequest
            {
                RentalType = request.RentalType,
                StartAt = request.StartAt.Value,
                EndAt = request.EndAt.Value,
            });

            if (!windowResult.IsSuccess)
                return ServiceResult<IReadOnlyList<VehicleCatalogItem>>.Failure(windowResult.ErrorCode!, windowResult.ErrorMessage!);

            window = windowResult.Data;
        }

        var filter = new VehicleSearchFilter(
            string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            window?.StartAt,
            window?.EndAt,
            request.Seats,
            string.IsNullOrWhiteSpace(request.Transmission) ? null : request.Transmission.Trim(),
            string.IsNullOrWhiteSpace(request.FuelType) ? null : request.FuelType.Trim(),
            string.IsNullOrWhiteSpace(request.BodyType) ? null : request.BodyType.Trim(),
            request.MinPrice,
            request.MaxPrice);

        var records = await _vehicleRepository.SearchAsync(filter, ct);

        var results = records.Select(v =>
        {
            int? billableUnits = window?.BillableUnits;
            decimal? quotedAmount = null;

            if (window is not null)
            {
                var unitPrice = window.RentalType == "Hourly" ? v.HourPrice : v.DayPrice;
                var baseAmount = unitPrice * window.BillableUnits;
                var discountPercent = window.RentalType == "Daily" && window.BillableUnits >= 7 && v.WeeklyDiscountEnabled
                    ? (v.WeeklyDiscountPercent ?? 0)
                    : 0;
                var discountAmount = decimal.Round(baseAmount * discountPercent / 100, 0, MidpointRounding.AwayFromZero);
                quotedAmount = baseAmount - discountAmount;
            }

            return new VehicleCatalogItem(
                v.Id,
                v.Brand,
                v.Model,
                v.Year,
                v.PlateNumber,
                v.BodyType,
                v.Seats,
                v.Transmission,
                v.Fuel,
                v.PickupAddress,
                v.Description,
                v.Features,
                v.HourPrice,
                v.DayPrice,
                v.Status,
                new VehicleOwnerSummary(v.OwnerId, v.OwnerName, v.OwnerEmail, v.OwnerPhone),
                v.ImageUrls,
                v.ImageUrls.FirstOrDefault(),
                v.WeeklyDiscountEnabled,
                v.WeeklyDiscountPercent,
                v.MileageLimitEnabled,
                v.DailyMileageLimitKm,
                v.OverageFeePerKm,
                v.RentalTerms,
                billableUnits,
                quotedAmount,
                window?.UnitLabel);
        }).ToList();

        return ServiceResult<IReadOnlyList<VehicleCatalogItem>>.Success(results);
    }
}
