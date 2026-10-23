namespace BonBon.DataAccess.Repositories.Interfaces;

public sealed record PaymentAttempt(
    Guid PaymentId, Guid BookingId, string Reference, decimal Amount, string Currency, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? CheckoutUrl,
    bool NeedsReconciliation = false, string? TransactionNo = null, DateTimeOffset? PaidAt = null);

public enum PaymentCreateOutcome
{
    Created, Reused, NotFound, Forbidden, Inactive, InvalidBooking, InvalidAmount,
    AlreadyPaid, ReconciliationRequired, InProgress,
}

public sealed record PaymentCreateResult(PaymentCreateOutcome Outcome, PaymentAttempt? Payment = null);

// These fields have already passed gateway signature and syntax verification.
public sealed record VerifiedPaymentNotification(
    string Reference, long AmountMinor, string ResponseCode, string TransactionStatus,
    string? TransactionNo, DateTimeOffset? PaidAt, string Fingerprint)
{
    public bool IsSuccess => ResponseCode == "00" && TransactionStatus == "00";
    // 07 may mean debited but suspected fraud; unknown/reversal/processing results
    // must not permit another charge until reconciliation.
    public bool IsDefinitiveFailure => TransactionStatus == "02" &&
        ResponseCode is "09" or "10" or "11" or "12" or "13" or "24" or "51" or "65" or "75" or "79";
}

public sealed record PaymentNotificationContext(
    PaymentAttempt Payment, string BookingStatus, decimal BookingAmount, DateTimeOffset BookingStartAt,
    bool ParticipantsActive, bool OtherSucceeded, bool TransactionUsedElsewhere, DateTimeOffset ReceivedAt);

public enum PaymentNotificationAction { Success, Failure, ReconcileSuccess, ReconcilePending, Duplicate, InvalidAmount, InvalidReference }
public sealed record PaymentNotificationDecision(PaymentNotificationAction Action, string? Reason = null);
public enum PaymentNotificationOutcome { Applied, Duplicate, NotFound, InvalidAmount }

public sealed class PaymentSchemaUnavailableException : Exception
{
    public PaymentSchemaUnavailableException() : base("Payment schema proposal v1 has not been installed and verified.") { }
}
