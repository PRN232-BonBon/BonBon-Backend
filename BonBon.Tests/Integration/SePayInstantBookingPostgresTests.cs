using System.Net;
using System.Net.Http.Json;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Bookings;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.Tests.Payments;
using Microsoft.Extensions.Options;

namespace BonBon.Tests.Integration;

public sealed class SePayInstantBookingPostgresTests
{
    [LocalPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Instant_hold_sepay_checkout_and_verified_ipn_are_handled_safely(bool late, bool withoutLegacyIndexes)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'");
        foreach (var file in new[] { "004_vnpay_sandbox.sql", "005_booking_quote_guard.sql", "006_momo_sandbox.sql", "007_sepay_sandbox.sql", "008_sepay_return_settlement.sql", "012_weekly_discount_duration.sql" })
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", file)));
        if (withoutLegacyIndexes)
            await db.ExecuteAsync("""
                DROP INDEX bonbon.uq_vnpay_sandbox_active;
                DROP INDEX bonbon.uq_vnpay_sandbox_idempotency;
                DROP INDEX bonbon.uq_vnpay_sandbox_accepted_success;
                DROP INDEX bonbon.uq_vnpay_sandbox_transaction;
                """);

        await using var context = db.CreateContext();
        var bookings = new BookingService(new BookingRepository(context));
        var quote = await bookings.QuoteAsync(new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(2) });
        Assert.True(quote.IsSuccess);
        var q = quote.Data!;
        var booking = await bookings.CreateAsync(db.Customer, new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(2),
            ExpectedQuote = new(q.BillableUnits, q.UnitPrice, q.WeeklyDiscountPercent, q.DiscountAmount, q.QuotedRentalAmount, q.IncludedMileageKm, q.OverageFeePerKm, q.RentalTerms) });
        Assert.True(booking.IsSuccess, booking.ErrorMessage);
        Assert.Equal("AwaitingPayment", booking.Data!.Status);

        SePayOrder? providerOrder = null;
        var handler = new SePayTestData.Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = providerOrder }) });
        var options = Options.Create(SePayTestData.Options);
        using var gateway = new SePayGateway(new HttpClient(handler), options, new SePaySignatureService());
        var repo = new SePayPaymentRepository(context);
        var checkout = new SePayCheckoutService(repo, gateway, options, Options.Create(new PaymentOptions()), TimeProvider.System);
        var result = await checkout.CreateAsync(db.Customer, new(booking.Data.BookingId, "SEPAY_SANDBOX"), "synthetic_request_key_12345", "127.0.0.1", default);
        Assert.True(result.IsSuccess, result.ErrorCode);
        Assert.NotNull(result.Data!.Checkout);
        var payment = await repo.Payments.GetByReferenceAsync(result.Data.PaymentId.ToString("N"));
        Assert.NotNull(payment);
        var hold = await db.ScalarAsync<DateTime>($"SELECT hold_expires_at FROM bonbon.booking WHERE booking_id='{booking.Data.BookingId}'");
        Assert.True(payment.ExpiresAt.UtcDateTime <= hold);
        providerOrder = SePayTestData.Order(payment, DateTimeOffset.UtcNow);
        TimeProvider clock = late ? new SePayTestData.Clock(payment.ExpiresAt.AddMinutes(1)) : TimeProvider.System;
        var ipn = new SePayIpnHandler(options, gateway, new SePayReconciliationService(repo, gateway, options, clock), repo, clock);
        Assert.Equal(200, await ipn.HandleAsync(SePayTestData.Options.IpnSecretKey, SePayTestData.Ipn(providerOrder, DateTimeOffset.UtcNow), default));
        Assert.Equal(late ? "AwaitingPayment" : "Confirmed", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{booking.Data.BookingId}'"));
        Assert.Equal(late, await db.ScalarAsync<bool>($"SELECT needs_reconciliation FROM bonbon.payment WHERE payment_id='{payment.PaymentId}'"));
        Assert.Equal(200, await ipn.HandleAsync(SePayTestData.Options.IpnSecretKey, SePayTestData.Ipn(providerOrder, DateTimeOffset.UtcNow), default));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.payment_notification WHERE payment_id='{payment.PaymentId}'"));
    }
}
