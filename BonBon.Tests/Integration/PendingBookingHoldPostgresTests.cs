using Npgsql;
using BonBon.API.Services;
using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;

namespace BonBon.Tests.Integration;

public class PendingBookingHoldPostgresTests
{
    [LocalPostgresFact]
    public async Task Legacy_vehicle_without_fuel_type_allows_booking_details_and_cancellation()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"UPDATE bonbon.vehicle SET fuel_type=NULL WHERE vehicle_id='{db.Vehicle}'");
        await using var context = db.CreateContext();
        var repository = new BookingRepository(context);
        var detail = await repository.GetByIdAsync(db.FirstBooking);
        Assert.NotNull(detail);
        Assert.Null(detail.FuelType);
        var service = new BookingService(repository);
        var result = await service.CancelAsync(db.FirstBooking, db.Customer);
        Assert.True(result.IsSuccess);
        Assert.Equal("Cancelled", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
    }

    [LocalPostgresFact]
    public async Task Legacy_cutover_converts_only_one_overlapping_pending_request_without_changing_prices()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        var cutover = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "convert_legacy_pending_to_instant.sql"));
        await db.ExecuteAsync(cutover);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='AwaitingPayment'"));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='PendingApproval'"));
        Assert.Equal(2L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND quoted_rental_amount=1300000"));
        Assert.True(await db.ScalarAsync<bool>($"SELECT hold_expires_at>clock_timestamp() AND hold_expires_at<=clock_timestamp()+interval '15 minutes' FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='AwaitingPayment'"));
        await db.ExecuteAsync(cutover);
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='AwaitingPayment'"));
    }

    [LocalPostgresFact]
    public async Task Legacy_cutover_does_not_change_preexisting_awaiting_payment()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"UPDATE bonbon.booking SET status='AwaitingPayment' WHERE booking_id='{db.FirstBooking}'");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "convert_legacy_pending_to_instant.sql")));
        Assert.Equal("AwaitingPayment", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
        Assert.Equal("PendingApproval", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{db.SecondBooking}'"));
        Assert.True(await db.ScalarAsync<bool>($"SELECT hold_expires_at IS NULL FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
    }

    [LocalPostgresFact]
    public async Task Instant_booking_can_be_paid_without_manual_approval()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "012_weekly_discount_duration.sql")));
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "004_vnpay_sandbox.sql")));
        await using var context = db.CreateContext();
        var bookings = new BookingService(new BookingRepository(context));
        var quote = await bookings.QuoteAsync(new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1) });
        Assert.True(quote.IsSuccess);
        var q = quote.Data!;
        var booking = await bookings.CreateAsync(db.Customer, new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = db.Start, EndAt = db.Start.AddDays(1),
            ExpectedQuote = new(q.BillableUnits, q.UnitPrice, q.WeeklyDiscountPercent, q.DiscountAmount, q.QuotedRentalAmount, q.IncludedMileageKm, q.OverageFeePerKm, q.RentalTerms) });
        Assert.True(booking.IsSuccess);
        Assert.Equal("AwaitingPayment", booking.Data!.Status);
        var payments = new PaymentRepository(context);
        var now = DateTimeOffset.UtcNow;
        var checkout = await payments.CreateAsync(db.Customer, booking.Data.BookingId, "instanttestkey12345", now, TimeSpan.FromMinutes(15),
            _ => "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?test=1");
        Assert.Equal(PaymentCreateOutcome.Created, checkout.Outcome);
        var notification = new VerifiedPaymentNotification(checkout.Payment!.Reference, (long)(checkout.Payment.Amount * 100), "00", "00", "12345", now, new string('B', 64));
        Assert.Equal(PaymentNotificationOutcome.Applied, await payments.ProcessNotificationAsync(notification, now, ctx => PaymentNotificationPolicy.Decide(ctx, notification)));
        Assert.Equal("Confirmed", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{booking.Data.BookingId}'"));
    }

    [LocalPostgresFact]
    public async Task Changed_quote_rolls_back_booking_and_does_not_hold_the_slot()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "012_weekly_discount_duration.sql")));
        await db.ExecuteAsync("UPDATE bonbon.vehicle SET day_price=900000");
        await using var context = db.CreateContext();
        var repo = new BookingRepository(context);
        var expected = new ExpectedBookingQuote(1, 650000, 0, 0, 650000, null, null, null);
        await Assert.ThrowsAsync<BookingQuoteChangedException>(() => repo.CreateAsync(db.Customer, db.Vehicle, "Daily", db.Start, db.Start.AddDays(1), expected));
        Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'"));
        Assert.True(await repo.IsVehicleAvailableAsync(db.Vehicle, db.Start, db.Start.AddDays(1)));
    }

    [LocalPostgresFact]
    public async Task Late_success_after_expiry_is_recorded_for_reconciliation_not_confirmed()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE booking_id='{db.SecondBooking}'");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "004_vnpay_sandbox.sql")));
        await db.ExecuteAsync($"UPDATE bonbon.booking SET status='AwaitingPayment', hold_expires_at=clock_timestamp()+interval '1 minute' WHERE booking_id='{db.FirstBooking}'");
        await using var context = db.CreateContext();
        var repository = new PaymentRepository(context);
        var now = DateTimeOffset.UtcNow;
        var payment = await repository.CreateAsync(db.Customer, db.FirstBooking, "testkey1234567890", now,
            TimeSpan.FromMinutes(15), _ => "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?test=1");
        Assert.Equal(PaymentCreateOutcome.Created, payment.Outcome);
        Assert.True(payment.Payment!.ExpiresAt <= now.AddMinutes(1).AddSeconds(1));
        await db.ExecuteAsync($"UPDATE bonbon.booking SET hold_expires_at=clock_timestamp()-interval '1 second' WHERE booking_id='{db.FirstBooking}'");
        await InstantBookingExpiryService.ExpireBatchAsync(context);
        var notification = new VerifiedPaymentNotification(payment.Payment.Reference, (long)(payment.Payment.Amount * 100), "00", "00", "12345", now, new string('A', 64));
        var outcome = await repository.ProcessNotificationAsync(notification, now.AddMinutes(2), ctx => PaymentNotificationPolicy.Decide(ctx, notification));
        Assert.Equal(PaymentNotificationOutcome.Applied, outcome);
        Assert.Equal("Cancelled", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
        Assert.True(await db.ScalarAsync<bool>($"SELECT needs_reconciliation FROM bonbon.payment WHERE payment_id='{payment.Payment.PaymentId}'"));
    }

    [LocalPostgresFact]
    public async Task Expiry_worker_releases_expired_holds_but_not_unexpired_or_legacy_bookings()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE booking_id='{db.SecondBooking}'");
        await db.ExecuteAsync($"UPDATE bonbon.booking SET status='AwaitingPayment', hold_expires_at=clock_timestamp()-interval '1 minute' WHERE booking_id='{db.FirstBooking}'");
        await using var context = db.CreateContext();
        await InstantBookingExpiryService.ExpireBatchAsync(context);
        Assert.Equal("Cancelled", await db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
        await db.ExecuteAsync($"INSERT INTO bonbon.booking (customer_account_id, vehicle_id, rental_type, billable_units, start_at, end_at, unit_price_snapshot, status, hold_expires_at) VALUES ('{db.Customer}', '{db.Vehicle}', 'Daily', 1, '{db.Start:O}', '{db.Start.AddDays(1):O}', 650000, 'AwaitingPayment', clock_timestamp()+interval '15 minutes')");
        await db.ExecuteAsync($"INSERT INTO bonbon.booking (customer_account_id, vehicle_id, rental_type, billable_units, start_at, end_at, unit_price_snapshot, status) VALUES ('{db.Customer}', '{db.Vehicle}', 'Daily', 1, '{db.Start.AddDays(2):O}', '{db.Start.AddDays(3):O}', 650000, 'AwaitingPayment')");
        await InstantBookingExpiryService.ExpireBatchAsync(context);
        Assert.Equal(2L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='AwaitingPayment'"));
    }

    [LocalPostgresTheory]
    [InlineData(167, 0)]
    [InlineData(168, 10)]
    public async Task Weekly_discount_uses_actual_duration(int hours, int expectedPercent)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id = '{db.Vehicle}'");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "011_pending_booking_holds.sql")));
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "012_weekly_discount_duration.sql")));
        await db.ExecuteAsync("UPDATE bonbon.vehicle SET weekly_discount_enabled=true, weekly_discount_percent=10");
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT (bonbon.create_booking(@customer, @vehicle, 'Daily', @start, @end)).weekly_discount_percent_snapshot", connection);
        command.Parameters.AddWithValue("customer", db.Customer);
        command.Parameters.AddWithValue("vehicle", db.Vehicle);
        command.Parameters.AddWithValue("start", db.Start);
        command.Parameters.AddWithValue("end", db.Start.AddHours(hours));
        Assert.Equal((decimal)expectedPercent, (decimal)(await command.ExecuteScalarAsync())!);
    }

    [LocalPostgresFact]
    public async Task Only_one_concurrent_pending_request_wins_and_cancellation_releases_slot()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"DELETE FROM bonbon.booking WHERE vehicle_id = '{db.Vehicle}'");
        await db.ExecuteAsync($"INSERT INTO bonbon.account_role (account_id, role_id, status) VALUES ('{db.Outsider}', 2, 'Active')");
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "012_weekly_discount_duration.sql")));
        var start = db.Start.AddDays(10);

        async Task<string> Create(Guid customer)
        {
            try
            {
                await using var connection = new NpgsqlConnection(db.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT (bonbon.create_booking(@customer, @vehicle, 'Daily', @start, @end)).booking_id", connection);
                command.Parameters.AddWithValue("customer", customer);
                command.Parameters.AddWithValue("vehicle", db.Vehicle);
                command.Parameters.AddWithValue("start", start);
                command.Parameters.AddWithValue("end", start.AddDays(1));
                return ((Guid)(await command.ExecuteScalarAsync())!).ToString();
            }
            catch (PostgresException e) when (e.SqlState == "23P01") { return "conflict"; }
        }

        var attempts = await Task.WhenAll(Create(db.Customer), Create(db.Outsider));
        Assert.Single(attempts, result => result == "conflict");
        var winner = Guid.Parse(Assert.Single(attempts, result => result != "conflict"));
        Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}' AND status='AwaitingPayment'"));
        await db.ExecuteAsync($"UPDATE bonbon.booking SET status='Cancelled' WHERE booking_id='{winner}'");
        Assert.NotEqual("conflict", await Create(db.Outsider));
    }

    [LocalPostgresFact]
    public async Task Migration_preserves_existing_overlapping_pending_bookings()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "011_pending_booking_holds.sql"));
        await db.ExecuteAsync(migration);
        Assert.Equal(2L, await db.ScalarAsync<long>($"SELECT count(*) FROM bonbon.booking WHERE vehicle_id='{db.Vehicle}'"));
    }
}
