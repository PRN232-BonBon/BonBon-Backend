using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Interfaces;

public sealed record PaymentVerification(bool IsValid, string? ErrorCode = null, VerifiedPaymentNotification? Notification = null);

public interface IPaymentGateway
{
    bool IsConfigured { get; }
    string CreateCheckoutUrl(PaymentAttempt payment, string ipAddress);
    PaymentVerification Verify(IReadOnlyDictionary<string, string> parameters);
}
