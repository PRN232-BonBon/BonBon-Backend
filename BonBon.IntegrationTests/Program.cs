using System;
using System.Linq;
using System.Threading.Tasks;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.DTOs.Vehicles;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BonBon.IntegrationTests;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("========================================================================");
        Console.WriteLine("        BONBON INTEGRATION TEST RUNNER (BE-01, BE-02, BE-05, BE-06)     ");
        Console.WriteLine("========================================================================");

        var services = new ServiceCollection();

        var mockBookingRepo = new MockBookingRepository();
        var mockVehicleRepo = new MockVehicleRepository(mockBookingRepo);

        services.AddSingleton<IBookingRepository>(mockBookingRepo);
        services.AddSingleton<IVehicleRepository>(mockVehicleRepo);
        services.AddScoped<IBookingService, BookingService>();
        services.AddScoped<IVehicleService, VehicleService>();

        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var bookingService = scope.ServiceProvider.GetRequiredService<IBookingService>();
        var vehicleService = scope.ServiceProvider.GetRequiredService<IVehicleService>();

        var customerId = Guid.Parse("c8333683-43d2-4f4c-bfbf-2c2eb0a43db4");
        var ownerId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var vehicleId = Guid.Parse("20000000-0000-0000-0000-000000000001");

        int failedTests = 0;

        void Assert(bool condition, string testName, string details = "")
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] {testName} {details}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] {testName} {details}");
                failedTests++;
            }
            Console.ResetColor();
        }

        try
        {
            // ────────────────────────────────────────────────────────────────
            // BE-01 — Hoàn thiện nghiệp vụ booking
            // ────────────────────────────────────────────────────────────────
            Console.WriteLine("\n--- [BE-01] HOÀN THIỆN NGHIỆP VỤ BOOKING ---");

            // 1.1 Chặn đặt trong quá khứ
            var pastResult = await bookingService.CreateAsync(customerId, new CreateBookingRequest
            {
                VehicleId = vehicleId,
                RentalType = "Hourly",
                StartAt = DateTimeOffset.UtcNow.AddDays(-2),
                EndAt = DateTimeOffset.UtcNow.AddDays(-2).AddHours(4)
            });
            Assert(!pastResult.IsSuccess && pastResult.ErrorCode == "PAST_START_TIME",
                "BE-01.1 Chặn đặt thời gian trong quá khứ",
                $"(ErrorCode: {pastResult.ErrorCode}, Message: {pastResult.ErrorMessage})");

            // 1.2 Chặn chủ xe tự thuê xe của mình
            var selfResult = await bookingService.CreateAsync(ownerId, new CreateBookingRequest
            {
                VehicleId = vehicleId,
                RentalType = "Daily",
                StartAt = DateTimeOffset.UtcNow.AddDays(25),
                EndAt = DateTimeOffset.UtcNow.AddDays(26)
            });
            Assert(!selfResult.IsSuccess && selfResult.ErrorCode == "SELF_RENTAL_NOT_ALLOWED",
                "BE-01.2 Chặn chủ xe tự thuê xe của chính mình",
                $"(ErrorCode: {selfResult.ErrorCode}, Message: {selfResult.ErrorMessage})");

            // 1.3 Tạo đơn hợp lệ (ngẫu nhiên tương lai để không trùng lặp các lần test trước)
            var futureDays = Random.Shared.Next(40, 100);
            var futureStart = DateTimeOffset.UtcNow.AddDays(futureDays);
            var futureEnd = futureStart.AddDays(1);
            var validBookingReq = new CreateBookingRequest
            {
                VehicleId = vehicleId,
                RentalType = "Daily",
                StartAt = futureStart,
                EndAt = futureEnd
            };

            var createResult = await bookingService.CreateAsync(customerId, validBookingReq);
            Assert(createResult.IsSuccess && createResult.Data != null && createResult.Data.Status == "PendingApproval",
                "BE-01.3 Tạo đơn đặt xe hợp lệ",
                $"(BookingId: {createResult.Data?.BookingId}, Status: {createResult.Data?.Status})");

            var bookingId = createResult.Data!.BookingId;

            // 1.4 Chống tạo đơn trùng lặp khi gửi lại request
            var duplicateResult = await bookingService.CreateAsync(customerId, validBookingReq);
            Assert(!duplicateResult.IsSuccess && duplicateResult.ErrorCode == "DUPLICATE_BOOKING",
                "BE-01.4 Chống tạo đơn trùng lặp (Idempotency)",
                $"(ErrorCode: {duplicateResult.ErrorCode}, Message: {duplicateResult.ErrorMessage})");

            // 1.5 Chủ xe duyệt đơn (kiểm tra lại xe & lịch)
            var approveResult = await bookingService.ApproveAsync(bookingId, ownerId);
            Assert(approveResult.IsSuccess,
                "BE-01.5 Chủ xe duyệt đơn thành công",
                $"(IsSuccess: {approveResult.IsSuccess})");

            // ────────────────────────────────────────────────────────────────
            // BE-02 — API tìm xe theo nhu cầu thuê & ảnh DB
            // ────────────────────────────────────────────────────────────────
            Console.WriteLine("\n--- [BE-02] TÌM XE THEO NHU CẦU & ẢNH DB ---");

            // 2.1 Tìm xe theo địa điểm
            var searchLoc = await vehicleService.SearchAsync(new VehicleSearchRequest
            {
                Location = "Thủ Đức"
            });
            Assert(searchLoc.IsSuccess && searchLoc.Data != null && searchLoc.Data.Count > 0,
                "BE-02.1 Tìm xe theo địa điểm 'Thủ Đức'",
                $"(Tìm thấy: {searchLoc.Data?.Count} xe)");

            var targetVehicle = searchLoc.Data?.FirstOrDefault(v => v.Id == vehicleId);
            Assert(targetVehicle != null && targetVehicle.Owner != null && !string.IsNullOrWhiteSpace(targetVehicle.Owner.FullName),
                "BE-02.2 Trả về thông tin chủ xe thực tế (FullName, Phone)",
                $"(Chủ xe: {targetVehicle?.Owner?.FullName}, Phone: {targetVehicle?.Owner?.Phone})");

            Assert(targetVehicle != null && !string.IsNullOrWhiteSpace(targetVehicle.ImageUrl),
                "BE-02.3 Trả về URL ảnh xe hỗ trợ bytea DB",
                $"(ImageUrl: {targetVehicle?.ImageUrl})");

            // 2.2 Lọc theo khung giờ đã kín lịch (phải loại bỏ xe đã được duyệt)
            var searchConflict = await vehicleService.SearchAsync(new VehicleSearchRequest
            {
                Location = "Thủ Đức",
                StartAt = futureStart.AddHours(1),
                EndAt = futureEnd.AddHours(-1),
                RentalType = "Daily"
            });
            var hasConflict = searchConflict.Data?.Any(v => v.Id == vehicleId) ?? false;
            Assert(searchConflict.IsSuccess && !hasConflict,
                "BE-02.4 Loại bỏ xe đã kín lịch (Approved Booking) khỏi kết quả tìm kiếm",
                $"(Số xe tìm thấy trong khung giờ trùng: {searchConflict.Data?.Count}, Chứa xe đã đặt: {hasConflict})");

            // ────────────────────────────────────────────────────────────────
            // BE-05 — Nhận xe, trả xe, hoàn thành chuyến
            // ────────────────────────────────────────────────────────────────
            Console.WriteLine("\n--- [BE-05] NHẬN XE, TRẢ XE & HOÀN THÀNH CHUYẾN ---");

            // 5.1 Không cho nhận xe khi chưa thanh toán (Status đang là Approved)
            var earlyPickup = await bookingService.ConfirmPickupAsync(bookingId, customerId, false, new ConfirmPickupRequest
            {
                OdometerKm = 50000,
                FuelLevel = 100
            });
            Assert(!earlyPickup.IsSuccess && earlyPickup.ErrorCode == "PAYMENT_REQUIRED",
                "BE-05.1 Chặn nhận xe khi chưa thanh toán (Status: Approved)",
                $"(ErrorCode: {earlyPickup.ErrorCode}, Message: {earlyPickup.ErrorMessage})");

            // 5.2 Xác nhận thanh toán -> Chuyển sang Confirmed
            var payResult = await bookingService.ConfirmPaymentAsync(bookingId, customerId, false);
            Assert(payResult.IsSuccess,
                "BE-05.2 Xác nhận thanh toán thành công -> Confirmed",
                $"(IsSuccess: {payResult.IsSuccess})");

            // 5.3 Chặn hoàn thành chuyến khi chưa trả xe
            var earlyComplete = await bookingService.CompleteBookingAsync(bookingId, customerId, false);
            Assert(!earlyComplete.IsSuccess && earlyComplete.ErrorCode == "INVALID_STATUS",
                "BE-05.3 Chặn hoàn thành chuyến khi chưa trả xe",
                $"(ErrorCode: {earlyComplete.ErrorCode}, Message: {earlyComplete.ErrorMessage})");

            // 5.4 Nhận xe hợp lệ -> Chuyển sang InProgress & ghi biên bản PICKUP
            var pickupResult = await bookingService.ConfirmPickupAsync(bookingId, customerId, false, new ConfirmPickupRequest
            {
                OdometerKm = 50000,
                FuelLevel = 100,
                ConditionNotes = "Xe mới, nội thất sạch sẽ"
            });
            Assert(pickupResult.IsSuccess && pickupResult.Data?.OdometerKm == 50000,
                "BE-05.4 Xác nhận nhận xe thành công -> InProgress",
                $"(InspectionId: {pickupResult.Data?.InspectionId}, ODO: {pickupResult.Data?.OdometerKm} km, Fuel: {pickupResult.Data?.FuelLevel}%)");

            // ────────────────────────────────────────────────────────────────
            // BE-06 — Phí vượt km & Trả xe
            // ────────────────────────────────────────────────────────────────
            Console.WriteLine("\n--- [BE-06] PHÍ VƯỢT KM & TRẢ XE ---");

            // 6.1 Trả xe với số km vượt hạn mức
            // Giả định hạn mức đã snapshot: 400 km.
            // Nhận: 50,000 km, Trả: 50,550 km -> Đi thực tế 550 km -> Vượt 150 km x 3,000 đ/km snapshot = 450,000 VND
            var returnResult = await bookingService.ConfirmReturnAsync(bookingId, customerId, false, new ConfirmReturnRequest
            {
                OdometerKm = 50550,
                FuelLevel = 95,
                ConditionNotes = "Xe bình thường, có vượt quãng đường"
            });

            Assert(returnResult.IsSuccess && returnResult.Data != null,
                "BE-06.1 Xác nhận trả xe thành công -> Returned",
                $"(ActualDistance: {returnResult.Data?.ActualDistanceKm} km, Included: {returnResult.Data?.IncludedMileageKm} km, Excess: {returnResult.Data?.ExcessKm} km, Fee: {returnResult.Data?.ExcessFeeAmount:N0} VND)");

            Assert(returnResult.Data?.ExcessKm == 150 && returnResult.Data?.ExcessFeeAmount == 450000,
                "BE-06.2 Tính chính xác phí vượt km theo snapshot (150 km x 3,000 = 450,000 VND)",
                $"(Vượt: {returnResult.Data?.ExcessKm} km, Phí: {returnResult.Data?.ExcessFeeAmount:N0} VND, ChargeId: {returnResult.Data?.ChargeId})");

            // 6.2 Chống ghi phí 2 lần (Idempotency) khi gọi lại tính phí vượt km
            var reCalc = await bookingService.CalculateExcessMileageAsync(bookingId, customerId, false);
            Assert(reCalc.IsSuccess && reCalc.Data?.ChargeId == returnResult.Data?.ChargeId && reCalc.Data?.ExcessFeeAmount == 450000,
                "BE-06.3 Chống ghi phụ phí trùng lặp (Idempotent call)",
                $"(Giữ nguyên ChargeId: {reCalc.Data?.ChargeId}, Amount: {reCalc.Data?.ExcessFeeAmount:N0} VND)");

            // 6.3 Hoàn thành chuyến sau khi đã trả xe
            var completeResult = await bookingService.CompleteBookingAsync(bookingId, customerId, false);
            Assert(completeResult.IsSuccess,
                "BE-05.5 Hoàn thành chuyến hợp lệ sau khi trả xe -> Completed",
                $"(IsSuccess: {completeResult.IsSuccess})");

            // 6.4 Kiểm tra BookingDetail có đủ cả 2 biên bản kiểm tra và phụ phí vượt km
            var detailResult = await bookingService.GetDetailAsync(bookingId, customerId, false);
            Assert(detailResult.IsSuccess && detailResult.Data?.Status == "Completed",
                "BE-05.6 Trạng thái đơn cuối cùng là 'Completed'",
                $"(Status: {detailResult.Data?.Status})");

            Assert(detailResult.Data?.Inspections.Count == 2,
                "BE-05.7 Lưu đủ 2 biên bản kiểm tra (PICKUP và RETURN)",
                $"(Số biên bản: {detailResult.Data?.Inspections.Count})");

            var excessCharge = detailResult.Data?.AdditionalCharges.FirstOrDefault(c => c.ReasonCode == "EXCESS_MILEAGE");
            Assert(excessCharge != null && excessCharge.Amount == 450000,
                "BE-06.4 Phụ phí EXCESS_MILEAGE được lưu và hiển thị chuẩn trong BookingDetail",
                $"(ReasonCode: {excessCharge?.ReasonCode}, Amount: {excessCharge?.Amount:N0} {excessCharge?.Currency})");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[EXCEPTION] {ex.Message}\n{ex.StackTrace}");
            Console.ResetColor();
            failedTests++;
        }

        Console.WriteLine("\n========================================================================");
        if (failedTests == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("                TẤT CẢ CÁC TEST CASES ĐÃ PASS 100%!                    ");
            Console.ResetColor();
            Console.WriteLine("========================================================================");
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"                 CÓ {failedTests} TEST CASE(S) THẤT BẠI!                ");
            Console.ResetColor();
            Console.WriteLine("========================================================================");
            return 1;
        }
    }
}
