using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.Business.Services.Interfaces;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.Extensions.Options;
using Moq;

namespace BonBon.Tests.Payments;

public class PaymentServiceTests
{
    private readonly Mock<IPaymentRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IPaymentGateway> _gateway = new(MockBehavior.Strict);
    private const string Key = "checkout_request_0001";
    private static readonly IReadOnlyDictionary<string, string> Query = new Dictionary<string, string>();
    private PaymentService Service(bool enabled = true)
    {
        if (enabled) _gateway.SetupGet(x => x.IsConfigured).Returns(true);
        return new(_repository.Object, _gateway.Object, Options.Create(new PaymentOptions { Enabled = enabled }), new PaymentTestData.FixedClock());
    }

    [Fact]
    public async Task Disabled_flag_never_calls_repository_or_gateway()
    {
        var service = Service(false);
        Assert.Equal("PAYMENT_DISABLED", (await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), Key, "127.0.0.1")).ErrorCode);
        Assert.Equal("PAYMENT_DISABLED", (await service.GetAsync(PaymentTestData.Customer, Guid.NewGuid())).ErrorCode);
        Assert.Equal("99", (await service.HandleIpnAsync(Query)).RspCode);
        Assert.Equal("PAYMENT_DISABLED", (await service.HandleReturnAsync(Query)).ErrorCode);
        Assert.False(new PaymentOptions().Enabled);
        _repository.VerifyNoOtherCalls();
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Signing_uses_only_locked_database_amount_and_persists_url_before_returning()
    {
        var payment = PaymentTestData.Attempt;
        var service = Service();
        _gateway.Setup(x => x.CreateCheckoutUrl(payment, "127.0.0.1")).Returns("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?signed=1");
        _repository.Setup(x => x.CreateAsync(PaymentTestData.Customer, PaymentTestData.Booking, Key,
                PaymentTestData.Now, TimeSpan.FromMinutes(15), It.IsAny<Func<PaymentAttempt, string>>(), default))
            .Returns<Guid, Guid, string, DateTimeOffset, TimeSpan, Func<PaymentAttempt, string>, CancellationToken>(
                (_, _, _, _, _, sign, _) => Task.FromResult(new PaymentCreateResult(PaymentCreateOutcome.Created,
                    payment with { CheckoutUrl = sign(payment) })));
        var result = await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), Key, "127.0.0.1");
        Assert.True(result.IsSuccess);
        Assert.Equal(1300000m, result.Data!.Amount);
        Assert.EndsWith("signed=1", result.Data.CheckoutUrl);
        _repository.VerifyAll();
        _gateway.VerifyAll();
    }

    [Theory]
    [InlineData(PaymentCreateOutcome.NotFound, "BOOKING_NOT_FOUND")]
    [InlineData(PaymentCreateOutcome.Forbidden, "PAYMENT_FORBIDDEN")]
    [InlineData(PaymentCreateOutcome.Inactive, "PAYMENT_ACTOR_INACTIVE")]
    [InlineData(PaymentCreateOutcome.InvalidBooking, "BOOKING_NOT_PAYABLE")]
    [InlineData(PaymentCreateOutcome.InvalidAmount, "PAYMENT_AMOUNT_INVALID")]
    [InlineData(PaymentCreateOutcome.AlreadyPaid, "BOOKING_ALREADY_PAID")]
    [InlineData(PaymentCreateOutcome.InProgress, "PAYMENT_IN_PROGRESS")]
    [InlineData(PaymentCreateOutcome.ReconciliationRequired, "PAYMENT_RECONCILIATION_REQUIRED")]
    public async Task Maps_payability_and_permission_failures(PaymentCreateOutcome outcome, string code)
    {
        var service = Service();
        SetupCreation().ReturnsAsync(new PaymentCreateResult(outcome));
        var result = await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), Key, "127.0.0.1");
        Assert.Equal(code, result.ErrorCode);
        _gateway.Verify(x => x.CreateCheckoutUrl(It.IsAny<PaymentAttempt>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Same_idempotency_key_reuses_existing_attempt_without_signing_again()
    {
        var service = Service();
        SetupCreation().ReturnsAsync(new PaymentCreateResult(PaymentCreateOutcome.Reused, PaymentTestData.Attempt));
        var result = await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), Key, "127.0.0.1");
        Assert.True(result.Data!.IsReplay);
        _gateway.Verify(x => x.CreateCheckoutUrl(It.IsAny<PaymentAttempt>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Missing_schema_returns_unavailable_without_exposing_database_detail()
    {
        var service = Service();
        SetupCreation().ThrowsAsync(new PaymentSchemaUnavailableException());
        Assert.Equal("PAYMENT_SCHEMA_UNAVAILABLE", (await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), Key, "127.0.0.1")).ErrorCode);
    }

    [Fact]
    public async Task Unconfigured_gateway_never_reaches_repository()
    {
        _gateway.SetupGet(x => x.IsConfigured).Returns(false);
        var service = new PaymentService(_repository.Object, _gateway.Object,
            Options.Create(new PaymentOptions { Enabled = true }), new PaymentTestData.FixedClock());
        Assert.Equal("PAYMENT_CONFIGURATION_INVALID", (await service.CreateAsync(PaymentTestData.Customer,
            new(PaymentTestData.Booking), Key, "127.0.0.1")).ErrorCode);
        _repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Status_lookup_is_scoped_to_authenticated_customer()
    {
        var service = Service();
        var id = Guid.NewGuid();
        _repository.Setup(x => x.GetForCustomerAsync(PaymentTestData.Customer, id, default)).ReturnsAsync((PaymentAttempt?)null);
        Assert.Equal("PAYMENT_NOT_FOUND", (await service.GetAsync(PaymentTestData.Customer, id)).ErrorCode);
        _repository.VerifyAll();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad key including spaces")]
    public async Task Rejects_bad_idempotency_key_before_database(string key)
    {
        var service = Service();
        Assert.Equal("PAYMENT_REQUEST_INVALID", (await service.CreateAsync(PaymentTestData.Customer, new(PaymentTestData.Booking), key, "127.0.0.1")).ErrorCode);
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(PaymentNotificationOutcome.Applied, "00")]
    [InlineData(PaymentNotificationOutcome.Duplicate, "02")]
    [InlineData(PaymentNotificationOutcome.NotFound, "01")]
    [InlineData(PaymentNotificationOutcome.InvalidAmount, "04")]
    public async Task Ipn_returns_protocol_acknowledgement(PaymentNotificationOutcome outcome, string code)
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(true, Notification: PaymentTestData.Notification));
        _repository.Setup(x => x.ProcessNotificationAsync(PaymentTestData.Notification, PaymentTestData.Now,
            It.IsAny<Func<PaymentNotificationContext, PaymentNotificationDecision>>(), default)).ReturnsAsync(outcome);
        Assert.Equal(code, (await service.HandleIpnAsync(Query)).RspCode);
    }

    [Theory]
    [InlineData("INVALID_SIGNATURE", "97")]
    [InlineData("INVALID_AMOUNT", "04")]
    [InlineData("INVALID_MERCHANT", "97")]
    public async Task Unverified_ipn_never_reaches_database(string error, string code)
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(false, error));
        Assert.Equal(code, (await service.HandleIpnAsync(Query)).RspCode);
        _repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false, PaymentNotificationAction.Success)]
    [InlineData(true, PaymentNotificationAction.ReconcileSuccess)]
    public async Task Ipn_passes_real_policy_to_locked_repository(bool late, PaymentNotificationAction action)
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(true, Notification: PaymentTestData.Notification));
        _repository.Setup(x => x.ProcessNotificationAsync(PaymentTestData.Notification, PaymentTestData.Now,
            It.IsAny<Func<PaymentNotificationContext, PaymentNotificationDecision>>(), default))
            .Returns<VerifiedPaymentNotification, DateTimeOffset, Func<PaymentNotificationContext, PaymentNotificationDecision>, CancellationToken>(
                (_, _, decide, _) =>
                {
                    var context = PaymentTestData.Context;
                    if (late) context = context with { Payment = context.Payment with { ExpiresAt = PaymentTestData.Now.AddSeconds(-1) } };
                    Assert.Equal(action, decide(context).Action);
                    return Task.FromResult(PaymentNotificationOutcome.Applied);
                });
        Assert.Equal("00", (await service.HandleIpnAsync(Query)).RspCode);
    }

    [Fact]
    public async Task Return_reads_database_status_and_never_processes_ipn()
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(true, Notification: PaymentTestData.Notification));
        _repository.Setup(x => x.GetByReferenceAsync("ABC123", default)).ReturnsAsync(PaymentTestData.Attempt);
        var result = await service.HandleReturnAsync(Query);
        Assert.True(result.IsSuccess);
        Assert.Equal("Pending", result.Data!.Status); // Signed browser success cannot change this.
        _repository.Verify(x => x.ProcessNotificationAsync(It.IsAny<VerifiedPaymentNotification>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<Func<PaymentNotificationContext, PaymentNotificationDecision>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Return_rejects_wrong_amount_against_stored_attempt()
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(true, Notification: PaymentTestData.Notification with { AmountMinor = 100 }));
        _repository.Setup(x => x.GetByReferenceAsync("ABC123", default)).ReturnsAsync(PaymentTestData.Attempt);
        Assert.Equal("PAYMENT_AMOUNT_INVALID", (await service.HandleReturnAsync(Query)).ErrorCode);
    }

    [Fact]
    public async Task Return_rejects_unknown_transaction_reference()
    {
        var service = Service();
        _gateway.Setup(x => x.Verify(Query)).Returns(new PaymentVerification(true, Notification: PaymentTestData.Notification with { Reference = "UNKNOWN" }));
        _repository.Setup(x => x.GetByReferenceAsync("UNKNOWN", default)).ReturnsAsync((PaymentAttempt?)null);
        Assert.Equal("PAYMENT_NOT_FOUND", (await service.HandleReturnAsync(Query)).ErrorCode);
    }

    [Fact]
    public async Task Expired_attempt_reports_reconciliation_without_any_write()
    {
        var service = Service();
        var payment = PaymentTestData.Attempt with { ExpiresAt = PaymentTestData.Now.AddSeconds(-1) };
        _repository.Setup(x => x.GetForCustomerAsync(PaymentTestData.Customer, payment.PaymentId, default)).ReturnsAsync(payment);
        var result = await service.GetAsync(PaymentTestData.Customer, payment.PaymentId);
        Assert.True(result.Data!.RequiresReconciliation);
    }

    private Moq.Language.Flow.ISetup<IPaymentRepository, Task<PaymentCreateResult>> SetupCreation() =>
        _repository.Setup(x => x.CreateAsync(PaymentTestData.Customer, PaymentTestData.Booking, Key, PaymentTestData.Now,
            TimeSpan.FromMinutes(15), It.IsAny<Func<PaymentAttempt, string>>(), default));
}
