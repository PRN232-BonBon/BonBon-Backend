using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Tests.Payments;

public class PaymentNotificationPolicyTests
{
    [Fact]
    public void Valid_receipt_requests_atomic_success() => Assert.Equal(PaymentNotificationAction.Success,
        PaymentNotificationPolicy.Decide(PaymentTestData.Context, PaymentTestData.Notification).Action);

    [Fact]
    public void Wrong_amount_never_confirms() => Assert.Equal(PaymentNotificationAction.InvalidAmount,
        PaymentNotificationPolicy.Decide(PaymentTestData.Context, PaymentTestData.Notification with { AmountMinor = 100 }).Action);

    [Fact]
    public void Wrong_reference_never_confirms() => Assert.Equal(PaymentNotificationAction.InvalidReference,
        PaymentNotificationPolicy.Decide(PaymentTestData.Context, PaymentTestData.Notification with { Reference = "OTHER" }).Action);

    [Theory]
    [InlineData("24")]
    [InlineData("51")]
    public void Definitive_failure_does_not_confirm(string code) => Assert.Equal(PaymentNotificationAction.Failure,
        PaymentNotificationPolicy.Decide(PaymentTestData.Context,
            PaymentTestData.Notification with { ResponseCode = code, TransactionStatus = "02" }).Action);

    [Fact]
    public void Processing_or_inconsistent_result_requires_reconciliation() => Assert.Equal(PaymentNotificationAction.ReconcilePending,
        PaymentNotificationPolicy.Decide(PaymentTestData.Context,
            PaymentTestData.Notification with { TransactionStatus = "01" }).Action);

    [Theory]
    [InlineData("07", "02")]
    [InlineData("07", "00")]
    [InlineData("99", "02")]
    [InlineData("00", "04")]
    public void Suspected_debit_and_unknown_results_never_enable_a_second_charge(string code, string status) =>
        Assert.Equal(PaymentNotificationAction.ReconcilePending, PaymentNotificationPolicy.Decide(PaymentTestData.Context,
            PaymentTestData.Notification with { ResponseCode = code, TransactionStatus = status }).Action);

    [Fact]
    public void Gateway_second_precision_does_not_reject_a_same_second_payment()
    {
        var context = PaymentTestData.Context;
        context = context with { Payment = context.Payment with { CreatedAt = PaymentTestData.Now.AddMilliseconds(500) } };
        Assert.Equal(PaymentNotificationAction.Success, PaymentNotificationPolicy.Decide(context, PaymentTestData.Notification).Action);
    }

    [Fact]
    public void Duplicate_success_and_later_failure_do_not_undo_receipt()
    {
        var context = PaymentTestData.Context;
        context = context with { Payment = context.Payment with { Status = "Succeeded", TransactionNo = "12345678" } };
        Assert.Equal(PaymentNotificationAction.Duplicate, PaymentNotificationPolicy.Decide(context, PaymentTestData.Notification).Action);
        Assert.Equal(PaymentNotificationAction.Duplicate, PaymentNotificationPolicy.Decide(context,
            PaymentTestData.Notification with { ResponseCode = "24", TransactionStatus = "02" }).Action);
    }

    [Theory]
    [InlineData("00", "04")]
    [InlineData("07", "00")]
    [InlineData("99", "02")]
    public void Reversal_or_uncertainty_after_success_requires_reconciliation(string code,string status)
    {
        var context=PaymentTestData.Context;
        context=context with { Payment=context.Payment with { Status="Succeeded",TransactionNo="12345678" } };
        var decision=PaymentNotificationPolicy.Decide(context,PaymentTestData.Notification with {ResponseCode=code,TransactionStatus=status});
        Assert.Equal(PaymentNotificationAction.ReconcilePending,decision.Action);
        Assert.Equal("GATEWAY_RESULT_AFTER_SUCCESS",decision.Reason);
    }

    [Theory]
    [InlineData("late", "LATE_PAYMENT")]
    [InlineData("booking", "BOOKING_NOT_PAYABLE")]
    [InlineData("price", "BOOKING_AMOUNT_CHANGED")]
    [InlineData("inactive", "PARTICIPANTS_INACTIVE")]
    [InlineData("other", "OTHER_PAYMENT_SUCCEEDED")]
    [InlineData("reused", "PROVIDER_TRANSACTION_REUSED")]
    [InlineData("failed", "SUCCESS_AFTER_TERMINAL_RESULT")]
    [InlineData("reconcile", "PREVIOUS_RECONCILIATION_REQUIRED")]
    [InlineData("pickup", "PICKUP_TIME_PASSED")]
    [InlineData("future", "INVALID_PAYMENT_TIME")]
    [InlineData("multiple", "MULTIPLE_PROVIDER_TRANSACTIONS")]
    public void Anomalous_money_is_recorded_for_reconciliation_without_confirming(string scenario, string reason)
    {
        var context = PaymentTestData.Context;
        var notification = PaymentTestData.Notification;
        context = scenario switch
        {
            "late" => context with { ReceivedAt = context.Payment.ExpiresAt.AddSeconds(1) },
            "booking" => context with { BookingStatus = "Cancelled" },
            "price" => context with { BookingAmount = 1500000m },
            "inactive" => context with { ParticipantsActive = false },
            "other" => context with { OtherSucceeded = true },
            "reused" => context with { TransactionUsedElsewhere = true },
            "failed" => context with { Payment = context.Payment with { Status = "Failed" } },
            "reconcile" => context with { Payment = context.Payment with { NeedsReconciliation = true } },
            "pickup" => context with { BookingStartAt = PaymentTestData.Now },
            "multiple" => context with { Payment = context.Payment with { Status = "Succeeded", TransactionNo = "87654321" } },
            _ => context,
        };
        if (scenario == "future") notification = notification with { PaidAt = PaymentTestData.Now.AddMinutes(3) };
        var decision = PaymentNotificationPolicy.Decide(context, notification);
        Assert.Equal(PaymentNotificationAction.ReconcileSuccess, decision.Action);
        Assert.Equal(reason, decision.Reason);
    }
}
