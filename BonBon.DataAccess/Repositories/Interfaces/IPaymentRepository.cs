namespace BonBon.DataAccess.Repositories.Interfaces;

public interface IPaymentRepository
{
    Task<PaymentCreateResult> CreateAsync(Guid customerId, Guid bookingId, string idempotencyKey,
        DateTimeOffset now, TimeSpan lifetime, Func<PaymentAttempt, string> buildCheckoutUrl, CancellationToken ct = default);
    Task<PaymentAttempt?> GetForCustomerAsync(Guid customerId, Guid paymentId, CancellationToken ct = default);
    Task<PaymentAttempt?> GetByReferenceAsync(string reference, CancellationToken ct = default);
    Task<PaymentNotificationOutcome> ProcessNotificationAsync(VerifiedPaymentNotification notification,
        DateTimeOffset receivedAt, Func<PaymentNotificationContext, PaymentNotificationDecision> decide, CancellationToken ct = default);
}
