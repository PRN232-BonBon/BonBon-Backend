using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Business.Services.Implementations;

public static class PaymentNotificationPolicy
{
    public static PaymentNotificationDecision Decide(PaymentNotificationContext context, VerifiedPaymentNotification notification)
    {
        var payment = context.Payment;
        if (payment.Reference != notification.Reference) return new(PaymentNotificationAction.InvalidReference);
        if (notification.AmountMinor != payment.Amount * 100 || payment.Currency != "VND")
            return new(PaymentNotificationAction.InvalidAmount);
        // A definitive failure must never undo a receipt. A reversal/uncertain
        // result after success is different: preserve the receipt and flag it.
        if (payment.Status == "Succeeded" && !notification.IsSuccess)
            return notification.IsDefinitiveFailure ? new(PaymentNotificationAction.Duplicate)
                : new(PaymentNotificationAction.ReconcilePending, "GATEWAY_RESULT_AFTER_SUCCESS");
        if (notification.IsSuccess)
        {
            if (payment.Status == "Succeeded" && payment.TransactionNo == notification.TransactionNo)
                return new(PaymentNotificationAction.Duplicate);
            var reason = payment.Status == "Succeeded" ? "MULTIPLE_PROVIDER_TRANSACTIONS"
                : context.TransactionUsedElsewhere ? "PROVIDER_TRANSACTION_REUSED"
                : context.OtherSucceeded ? "OTHER_PAYMENT_SUCCEEDED"
                : payment.Status is "Failed" or "Cancelled" ? "SUCCESS_AFTER_TERMINAL_RESULT"
                : payment.NeedsReconciliation ? "PREVIOUS_RECONCILIATION_REQUIRED"
                : notification.PaidAt is null || notification.PaidAt < payment.CreatedAt.AddSeconds(-1) || notification.PaidAt > context.ReceivedAt.AddMinutes(2) ? "INVALID_PAYMENT_TIME"
                : context.ReceivedAt > payment.ExpiresAt || notification.PaidAt > payment.ExpiresAt ? "LATE_PAYMENT"
                : context.BookingStatus != "AwaitingPayment" ? "BOOKING_NOT_PAYABLE"
                : context.BookingAmount != payment.Amount ? "BOOKING_AMOUNT_CHANGED"
                : context.BookingStartAt <= context.ReceivedAt ? "PICKUP_TIME_PASSED"
                : !context.ParticipantsActive ? "PARTICIPANTS_INACTIVE" : null;
            return reason is null ? new(PaymentNotificationAction.Success) : new(PaymentNotificationAction.ReconcileSuccess, reason);
        }
        if (notification.IsDefinitiveFailure)
            return payment.Status is "Failed" or "Cancelled" ? new(PaymentNotificationAction.Duplicate) : new(PaymentNotificationAction.Failure);
        return new(PaymentNotificationAction.ReconcilePending, "GATEWAY_RESULT_UNCERTAIN");
    }
}
