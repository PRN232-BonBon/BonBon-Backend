using BonBon.Business.Common;
using BonBon.Business.DTOs.Payments;

namespace BonBon.Business.Services.Interfaces;

public interface IPaymentService
{
    Task<ServiceResult<PaymentCheckoutResponse>> CreateAsync(Guid customerId, CreatePaymentRequest request,
        string idempotencyKey, string ipAddress, CancellationToken ct = default);
    Task<ServiceResult<PaymentStatusResponse>> GetAsync(Guid customerId, Guid paymentId, CancellationToken ct = default);
    Task<VnPayIpnResponse> HandleIpnAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);
    Task<ServiceResult<PaymentReturnResponse>> HandleReturnAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken ct = default);
}
