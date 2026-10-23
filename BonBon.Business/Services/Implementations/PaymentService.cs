using System.Net;
using System.Text.RegularExpressions;
using BonBon.Business.Common;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class PaymentService(IPaymentRepository repository, IPaymentGateway gateway,
    IOptions<PaymentOptions> options, TimeProvider clock) : IPaymentService
{
    private string? UnavailableCode => !options.Value.Enabled ? "PAYMENT_DISABLED"
        : !gateway.IsConfigured || options.Value.CheckoutMinutes is < 1 or > 30 ? "PAYMENT_CONFIGURATION_INVALID" : null;

    public async Task<ServiceResult<PaymentCheckoutResponse>> CreateAsync(Guid customerId, CreatePaymentRequest request,
        string idempotencyKey, string ipAddress, CancellationToken ct = default)
    {
        if (UnavailableCode is { } unavailable) return Unavailable<PaymentCheckoutResponse>(unavailable);
        if (customerId == Guid.Empty || request.BookingId == Guid.Empty ||
            string.IsNullOrEmpty(idempotencyKey) || !Regex.IsMatch(idempotencyKey, "\\A[A-Za-z0-9_-]{16,100}\\z") || !IPAddress.TryParse(ipAddress, out _))
            return ServiceResult<PaymentCheckoutResponse>.Failure("PAYMENT_REQUEST_INVALID", "Booking, idempotency key hoặc địa chỉ kết nối không hợp lệ.");
        try
        {
            var result = await repository.CreateAsync(customerId, request.BookingId, idempotencyKey,
                clock.GetUtcNow(), TimeSpan.FromMinutes(options.Value.CheckoutMinutes),
                payment => gateway.CreateCheckoutUrl(payment, ipAddress), ct);
            if (result.Outcome is PaymentCreateOutcome.Created or PaymentCreateOutcome.Reused)
            {
                var payment = result.Payment ?? throw new InvalidOperationException("Missing payment snapshot.");
                return ServiceResult<PaymentCheckoutResponse>.Success(new(payment.PaymentId, payment.BookingId,
                    payment.Amount, payment.Currency, payment.Status,
                    payment.Status == "Pending" && !NeedsReconciliation(payment) ? payment.CheckoutUrl : null,
                    payment.ExpiresAt, result.Outcome == PaymentCreateOutcome.Reused));
            }
            var (code, detail) = result.Outcome switch
            {
                PaymentCreateOutcome.NotFound => ("BOOKING_NOT_FOUND", "Không tìm thấy đơn đặt xe."),
                PaymentCreateOutcome.Forbidden => ("PAYMENT_FORBIDDEN", "Bạn không phải khách hàng của đơn đặt xe."),
                PaymentCreateOutcome.Inactive => ("PAYMENT_ACTOR_INACTIVE", "Tài khoản hoặc vai trò của các bên không còn hoạt động."),
                PaymentCreateOutcome.InvalidBooking => ("BOOKING_NOT_PAYABLE", "Chỉ được thanh toán đơn đang chờ thanh toán và chưa đến giờ nhận xe."),
                PaymentCreateOutcome.InvalidAmount => ("PAYMENT_AMOUNT_INVALID", "Số tiền không đáp ứng giới hạn VNPay hoặc không phải VND nguyên."),
                PaymentCreateOutcome.AlreadyPaid => ("BOOKING_ALREADY_PAID", "Đơn đặt xe đã có khoản thanh toán thành công."),
                PaymentCreateOutcome.InProgress => ("PAYMENT_IN_PROGRESS", "Đã có lần thanh toán đang xử lý. Hãy dùng lại yêu cầu ban đầu hoặc kiểm tra trạng thái."),
                PaymentCreateOutcome.ReconciliationRequired => ("PAYMENT_RECONCILIATION_REQUIRED", "Giao dịch cần đối soát trước khi tạo lần thanh toán mới."),
                _ => throw new InvalidOperationException("Unknown payment creation outcome."),
            };
            return ServiceResult<PaymentCheckoutResponse>.Failure(code, detail);
        }
        catch (PaymentSchemaUnavailableException) { return Unavailable<PaymentCheckoutResponse>("PAYMENT_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { return Unavailable<PaymentCheckoutResponse>("PAYMENT_TEMPORARILY_UNAVAILABLE"); }
        catch (ArgumentException) { return ServiceResult<PaymentCheckoutResponse>.Failure("PAYMENT_REQUEST_INVALID", "Không thể tạo URL thanh toán từ dữ liệu hiện tại."); }
    }

    public async Task<ServiceResult<PaymentStatusResponse>> GetAsync(Guid customerId, Guid paymentId, CancellationToken ct = default)
    {
        if (UnavailableCode is { } unavailable) return Unavailable<PaymentStatusResponse>(unavailable);
        if (customerId == Guid.Empty || paymentId == Guid.Empty)
            return ServiceResult<PaymentStatusResponse>.Failure("PAYMENT_REQUEST_INVALID", "Mã thanh toán không hợp lệ.");
        try
        {
            var payment = await repository.GetForCustomerAsync(customerId, paymentId, ct);
            return payment is null ? ServiceResult<PaymentStatusResponse>.Failure("PAYMENT_NOT_FOUND", "Không tìm thấy thanh toán của tài khoản hiện tại.")
                : ServiceResult<PaymentStatusResponse>.Success(new(payment.PaymentId, payment.BookingId, payment.Amount,
                    payment.Currency, payment.Status, payment.ExpiresAt, NeedsReconciliation(payment)));
        }
        catch (PaymentSchemaUnavailableException) { return Unavailable<PaymentStatusResponse>("PAYMENT_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { return Unavailable<PaymentStatusResponse>("PAYMENT_TEMPORARILY_UNAVAILABLE"); }
    }

    public async Task<VnPayIpnResponse> HandleIpnAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (UnavailableCode is not null) return new("99", "Payment unavailable");
        var verified = gateway.Verify(parameters);
        if (!verified.IsValid) return new(verified.ErrorCode == "INVALID_AMOUNT" ? "04" : "97", "Invalid notification");
        var notification = verified.Notification!;
        try
        {
            var outcome = await repository.ProcessNotificationAsync(notification, clock.GetUtcNow(),
                context => PaymentNotificationPolicy.Decide(context, notification), ct);
            return outcome switch
            {
                PaymentNotificationOutcome.Applied => new("00", "Confirm Success"),
                PaymentNotificationOutcome.Duplicate => new("02", "Order already confirmed"),
                PaymentNotificationOutcome.NotFound => new("01", "Order not found"),
                PaymentNotificationOutcome.InvalidAmount => new("04", "Invalid amount"),
                _ => new("99", "Processing error"),
            };
        }
        catch (PaymentSchemaUnavailableException) { return new("99", "Payment schema unavailable"); }
        catch (NpgsqlException) { return new("99", "Retry notification later"); }
    }

    public async Task<ServiceResult<PaymentReturnResponse>> HandleReturnAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default)
    {
        if (UnavailableCode is { } unavailable) return Unavailable<PaymentReturnResponse>(unavailable);
        var verified = gateway.Verify(parameters);
        if (!verified.IsValid) return ServiceResult<PaymentReturnResponse>.Failure("PAYMENT_RETURN_INVALID", "Kết quả trả về không hợp lệ.");
        var notification = verified.Notification!;
        try
        {
            var payment = await repository.GetByReferenceAsync(notification.Reference, ct);
            if (payment is null) return ServiceResult<PaymentReturnResponse>.Failure("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch.");
            if (notification.AmountMinor != payment.Amount * 100)
                return ServiceResult<PaymentReturnResponse>.Failure("PAYMENT_AMOUNT_INVALID", "Số tiền trả về không khớp giao dịch.");
            // The browser redirect is never used as a write/confirmation path.
            return ServiceResult<PaymentReturnResponse>.Success(new(payment.PaymentId, payment.Status,
                NeedsReconciliation(payment), "Trạng thái lấy từ Backend. IPN và đối soát quyết định kết quả; URL trả về không xác nhận thanh toán."));
        }
        catch (PaymentSchemaUnavailableException) { return Unavailable<PaymentReturnResponse>("PAYMENT_SCHEMA_UNAVAILABLE"); }
        catch (NpgsqlException) { return Unavailable<PaymentReturnResponse>("PAYMENT_TEMPORARILY_UNAVAILABLE"); }
    }

    private bool NeedsReconciliation(PaymentAttempt payment) => payment.NeedsReconciliation ||
        (payment.Status is "Pending" or "Processing" && payment.ExpiresAt <= clock.GetUtcNow());
    private static ServiceResult<T> Unavailable<T>(string code) =>
        ServiceResult<T>.Failure(code, "Payment chưa sẵn sàng. Cần cấu hình sandbox, schema đã được duyệt và môi trường kiểm thử phù hợp.");
}
