using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using BonBon.Business.Configuration;
using BonBon.Business.DTOs.Payments;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.Tests.Payments;
using Microsoft.Extensions.Options;

namespace BonBon.Tests.Integration;

public sealed class ReturnSettlementPostgresTests
{
    [LocalPostgresTheory]
    [InlineData(10300, 0, false)]
    [InlineData(10550, 450000, false)]
    [InlineData(10550, 450000, true)]
    public async Task Owner_reviews_return_and_only_verified_surcharge_payment_finishes_trip(decimal returnKm, decimal expectedFee, bool late)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'");
        foreach (var file in new[] { "004_vnpay_sandbox.sql", "005_booking_quote_guard.sql", "006_momo_sandbox.sql", "007_sepay_sandbox.sql", "008_sepay_return_settlement.sql", "012_weekly_discount_duration.sql" })
            await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", file)));
        await db.ExecuteAsync($"UPDATE bonbon.vehicle SET mileage_limit_enabled=true,daily_mileage_limit_km=400,overage_fee_per_km=3000 WHERE vehicle_id='{db.Vehicle}'");
        await using var context = db.CreateContext();
        var bookings = new BookingService(new BookingRepository(context));
        var quote = await bookings.QuoteAsync(new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1) });
        Assert.True(quote.IsSuccess, quote.ErrorMessage);
        var q = quote.Data!;
        var created = await bookings.CreateAsync(db.Customer, new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1),
            ExpectedQuote = new(q.BillableUnits, q.UnitPrice, q.WeeklyDiscountPercent, q.DiscountAmount, q.QuotedRentalAmount, q.IncludedMileageKm, q.OverageFeePerKm, q.RentalTerms) });
        Assert.True(created.IsSuccess, created.ErrorMessage);
        var id = created.Data!.BookingId;
        SePayOrder? order = null;
        var handler = new SePayTestData.Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = order }) });
        var options = Options.Create(SePayTestData.Options);
        using var gateway = new SePayGateway(new HttpClient(handler), options, new SePaySignatureService());
        var repo = new SePayPaymentRepository(context);
        var checkout = new SePayCheckoutService(repo, gateway, options, Options.Create(new PaymentOptions()), TimeProvider.System);
        var ipn = new SePayIpnHandler(options, gateway, new SePayReconciliationService(repo, gateway, options, TimeProvider.System), repo, TimeProvider.System);
        static SePayOrder MakeOrder(BonBon.DataAccess.Repositories.Interfaces.PaymentAttempt payment, DateTimeOffset? now = null)
        {
            var sample = SePayTestData.Order(payment, now ?? DateTimeOffset.UtcNow);
            var amount = payment.Amount.ToString("0", CultureInfo.InvariantCulture);
            return sample with { Amount = amount, Transactions = [sample.Transactions![0] with { Amount = amount }] };
        }

        var rental = await checkout.CreateAsync(db.Customer, new(id), "return_test_rental_key_12345", "127.0.0.1", default);
        Assert.True(rental.IsSuccess, rental.ErrorCode);
        var rentalPayment = (await repo.Payments.GetByReferenceAsync(rental.Data!.PaymentId.ToString("N")))!;
        order = MakeOrder(rentalPayment);
        var initialIpn = await ipn.HandleAsync(SePayTestData.Options.IpnSecretKey, SePayTestData.Ipn(order, DateTimeOffset.UtcNow), default);
        Assert.True(initialIpn == 200, $"IPN returned {initialIpn}; reason: {await db.ScalarAsync<string>("SELECT coalesce((SELECT reason FROM bonbon.sepay_rejected_notification ORDER BY received_at DESC LIMIT 1),'none')")}");
        await using var firstContext = db.CreateContext();
        await using var secondContext = db.CreateContext();
        var firstService = new BookingService(new BookingRepository(firstContext));
        var secondService = new BookingService(new BookingRepository(secondContext));
        var pickups = await Task.WhenAll(
            firstService.ConfirmPickupAsync(id, db.Customer, false, new() { OdometerKm = 10000 }),
            secondService.ConfirmPickupAsync(id, db.Customer, false, new() { OdometerKm = 10000 }));
        Assert.Single(pickups, result => result.IsSuccess);
        Assert.False((await bookings.ConfirmReturnAsync(id, db.Owner, false, new() { OdometerKm = returnKm })).IsSuccess);
        var returns = await Task.WhenAll(
            firstService.ConfirmReturnAsync(id, db.Customer, false, new() { OdometerKm = returnKm }),
            secondService.ConfirmReturnAsync(id, db.Customer, false, new() { OdometerKm = returnKm }));
        var returned = Assert.Single(returns, result => result.IsSuccess);
        Assert.True(returned.IsSuccess, returned.ErrorMessage);
        Assert.Equal(expectedFee, returned.Data!.ExcessFeeAmount);
        Assert.Equal("Returned", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
        Assert.False((await bookings.CompleteBookingAsync(id, db.Customer)).IsSuccess);
        Assert.True((await bookings.CompleteBookingAsync(id, db.Owner)).IsSuccess);
        if (expectedFee == 0)
        {
            Assert.Equal("Completed", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
            return;
        }
        Assert.Equal("AwaitingSettlement", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
        Assert.False((await bookings.CompleteBookingAsync(id, db.Owner)).IsSuccess);
        var fee = await checkout.CreateAsync(db.Customer, new(id), "return_test_surcharge_key_12345", "127.0.0.1", default);
        Assert.True(fee.IsSuccess, fee.ErrorCode);
        Assert.Equal(expectedFee, fee.Data!.Amount);
        var feePayment = (await repo.Payments.GetByReferenceAsync(fee.Data.PaymentId.ToString("N")))!;
        Assert.Equal("ExcessMileage", feePayment.Purpose);
        var paymentTime = late ? feePayment.ExpiresAt.AddMinutes(1) : DateTimeOffset.UtcNow;
        var settlementClock = late ? new SePayTestData.Clock(paymentTime) : TimeProvider.System;
        var settlementIpn = new SePayIpnHandler(options, gateway, new SePayReconciliationService(repo, gateway, options, settlementClock), repo, settlementClock);
        order = MakeOrder(feePayment, paymentTime);
        var notification = SePayTestData.Ipn(order, paymentTime);
        Assert.NotEqual(200, await settlementIpn.HandleAsync("wrong-secret", notification, default));
        Assert.Equal("AwaitingSettlement", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
        Assert.Equal(200, await settlementIpn.HandleAsync(SePayTestData.Options.IpnSecretKey, notification, default));
        if (late)
        {
            Assert.Equal("AwaitingSettlement", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
            Assert.True(await db.ScalarAsync<bool>($"SELECT needs_reconciliation FROM bonbon.payment WHERE payment_id='{feePayment.PaymentId}'"));
            Assert.Equal("Accepted", await db.ScalarAsync<string>($"SELECT status FROM bonbon.additional_charge WHERE booking_id='{id}' AND reason_code='EXCESS_MILEAGE'"));
            return;
        }
        Assert.Equal(200, await settlementIpn.HandleAsync(SePayTestData.Options.IpnSecretKey, notification, default));
        Assert.Equal("Completed", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{id}'"));
        Assert.Equal("Paid", await db.ScalarAsync<string>($"SELECT status FROM bonbon.additional_charge WHERE booking_id='{id}' AND reason_code='EXCESS_MILEAGE'"));
        Assert.Equal(2L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.payment WHERE booking_id='{id}' AND status='Succeeded'"));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.payment_notification WHERE payment_id='{feePayment.PaymentId}'"));
    }
}
