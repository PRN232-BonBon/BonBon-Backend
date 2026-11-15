using BonBon.Business.Common;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BonBon.Business.Services.Implementations;

public sealed class SePayCheckoutService(ISePayPaymentRepository repository, ISePayGateway gateway,
    IOptions<SePayOptions> config, IOptions<PaymentOptions> paymentConfig, TimeProvider clock) : ISePayCheckoutService
{
    private sealed class Reservation(ISePayGateway gateway) : IPaymentGateway {
        public bool IsConfigured => gateway.IsConfigured;
        public string CreateCheckoutUrl(PaymentAttempt p, string ip) => gateway.MerchantBinding;
        public PaymentVerification Verify(IReadOnlyDictionary<string, string> fields) => new(false, "UNSUPPORTED");
    }
    // Reuse all existing ownership, account/role, vehicle, booking and idempotency checks.
    private PaymentService Common => new(repository.Payments, new Reservation(gateway),
        Options.Create(new PaymentOptions { Enabled = config.Value.Enabled, CheckoutMinutes = paymentConfig.Value.CheckoutMinutes }), clock);
    public async Task<ServiceResult<SePayCheckoutResponse>> CreateAsync(Guid customer, CreatePaymentRequest request, string key, string ip, CancellationToken ct)
    {
        var reserved = await Common.CreateAsync(customer, request, key, ip, ct);
        if (!reserved.IsSuccess) return ServiceResult<SePayCheckoutResponse>.Failure(reserved.ErrorCode!, reserved.ErrorMessage!);
        var r = reserved.Data!;
        try {
            var p = await repository.Payments.GetForCustomerAsync(customer, r.PaymentId, ct);
            if (p is null) return ServiceResult<SePayCheckoutResponse>.Failure("PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch.");
            if (p.CheckoutUrl != gateway.MerchantBinding)
                return ServiceResult<SePayCheckoutResponse>.Failure("PAYMENT_RECONCILIATION_REQUIRED", "Merchant đã thay đổi. Cần đối soát giao dịch cũ.");
            var form = p.Status == "Pending" && !p.NeedsReconciliation && p.ExpiresAt > clock.GetUtcNow() ? gateway.BuildForm(p) : null;
            return ServiceResult<SePayCheckoutResponse>.Success(new(p.PaymentId, p.BookingId, "SEPAY_SANDBOX", p.Amount,
                p.Currency, p.Status, p.ExpiresAt, r.IsReplay, form));
        } catch (PaymentSchemaUnavailableException) { return Failed("PAYMENT_SCHEMA_UNAVAILABLE"); }
          catch (NpgsqlException) { return Failed("PAYMENT_TEMPORARILY_UNAVAILABLE"); }
          catch (ArgumentException) { return Failed("PAYMENT_CONFIGURATION_INVALID"); }
    }
    private static ServiceResult<SePayCheckoutResponse> Failed(string code) => ServiceResult<SePayCheckoutResponse>.Failure(code, "SePay chưa sẵn sàng. Không tạo attempt mới khi kết quả chưa rõ.");
    public Task<ServiceResult<PaymentStatusResponse>> GetAsync(Guid customer, Guid payment, CancellationToken ct) => Common.GetAsync(customer, payment, ct);
}
