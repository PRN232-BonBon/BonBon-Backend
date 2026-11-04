using BonBon.Business.Configuration;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Tests.Payments;

internal static class PaymentTestData
{
    // Synthetic test vector only; never an operational merchant credential.
    public const string TestSecret = "0123456789ABCDEF0123456789ABCDEF";
    public static readonly DateTimeOffset Now = new(2026, 10, 23, 2, 0, 0, TimeSpan.Zero);
    public static readonly Guid Customer = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Booking = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public static PaymentAttempt Attempt => new(Guid.NewGuid(), Booking, "ABC123", 1300000m, "VND", "Pending",
        Now.AddMinutes(-2), Now.AddMinutes(13), "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?test=1");
    public static VerifiedPaymentNotification Notification => new("ABC123", 130000000, "00", "00", "12345678", Now, new string('A', 64));
    public static VnPayOptions GatewayOptions => new()
    {
        TmnCode = "TEST0001", HashSecret = TestSecret, ReturnUrl = "https://api.example.test/api/payments/vnpay/return",
    };
    public static PaymentNotificationContext Context => new(Attempt, "AwaitingPayment", 1300000m, Now.AddDays(1), true, false, false, Now);
    public sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
