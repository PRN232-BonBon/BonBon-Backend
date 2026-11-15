using BonBon.Business.Common;
using BonBon.Business.DTOs.Payments;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Interfaces;

public interface ISePaySignatureService
{
    string Sign(IReadOnlyDictionary<string, string> fields, string secret);
    bool Verify(IReadOnlyDictionary<string, string> fields, string secret, string signature);
}
public interface ISePayGateway
{
    bool IsConfigured { get; }
    string MerchantBinding { get; }
    SePayForm BuildForm(PaymentAttempt payment);
    Task<SePayQueryResult> QueryAsync(string invoice, CancellationToken ct);
}
public interface ISePayCheckoutService
{
    Task<ServiceResult<SePayCheckoutResponse>> CreateAsync(Guid customer, CreatePaymentRequest request, string key, string ip, CancellationToken ct);
    Task<ServiceResult<PaymentStatusResponse>> GetAsync(Guid customer, Guid payment, CancellationToken ct);
}
public interface ISePayIpnHandler { Task<int> HandleAsync(string secret, SePayIpn ipn, CancellationToken ct); }
public interface ISePayReconciliationService
{
    Task<int> ReconcileAsync(string invoice, CancellationToken ct);
    Task<int> ApplyAsync(string invoice, SePayIpn? ipn, CancellationToken ct);
}
