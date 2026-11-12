using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using BonBon.DataAccess.Repositories.Interfaces;
using Npgsql;

namespace BonBon.Tests.Integration;

public class BookingDecisionPostgresTests
{
    [LocalPostgresFact]
    public async Task Approve_preserves_quote_and_replay_does_not_write_again()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync("UPDATE bonbon.vehicle SET day_price = 900000");
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        var first = await service.ApproveAsync(db.Owner, db.FirstBooking);
        var second = await service.ApproveAsync(db.Owner, db.FirstBooking);
        Assert.True(first.IsSuccess);
        Assert.False(first.Data!.IsReplay);
        Assert.True(second.Data!.IsReplay);
        Assert.Equal(650000m, first.Data.UnitPrice);
        Assert.Equal(1300000m, first.Data.QuotedRentalAmount);
        Assert.Equal(1300000m, await db.ScalarAsync<decimal>(
            $"SELECT quoted_rental_amount FROM bonbon.booking WHERE booking_id = '{db.FirstBooking}'"));
        var conflict = await service.RejectAsync(db.Owner, db.FirstBooking);
        Assert.Equal("BOOKING_STATE_CONFLICT", conflict.ErrorCode);
    }

    [LocalPostgresFact]
    public async Task Reject_is_idempotent_and_cannot_later_be_approved()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.True((await service.RejectAsync(db.Owner, db.FirstBooking)).IsSuccess);
        Assert.True((await service.RejectAsync(db.Owner, db.FirstBooking)).Data!.IsReplay);
        Assert.Equal("BOOKING_STATE_CONFLICT", (await service.ApproveAsync(db.Owner, db.FirstBooking)).ErrorCode);
    }

    [LocalPostgresFact]
    public async Task Missing_booking_and_other_owner_never_change_booking()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.Equal("BOOKING_NOT_FOUND", (await service.ApproveAsync(db.Owner, Guid.NewGuid())).ErrorCode);
        Assert.Equal("BOOKING_NOT_OWNER", (await service.ApproveAsync(db.Outsider, db.FirstBooking)).ErrorCode);
        Assert.Equal("BOOKING_NOT_OWNER", (await service.RejectAsync(db.Outsider, db.FirstBooking)).ErrorCode);
        Assert.Equal("PendingApproval", await StatusAsync(db, db.FirstBooking));
    }

    [LocalPostgresTheory]
    [InlineData("owner-account", "OWNER_INACTIVE")]
    [InlineData("owner-role", "OWNER_INACTIVE")]
    [InlineData("customer-account", "CUSTOMER_INACTIVE")]
    [InlineData("customer-role", "CUSTOMER_INACTIVE")]
    [InlineData("vehicle", "VEHICLE_UNAVAILABLE")]
    public async Task Rechecks_current_eligibility(string suspended, string error)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        var sql = suspended switch
        {
            "owner-account" => $"UPDATE bonbon.account SET status = 'Suspended' WHERE account_id = '{db.Owner}'",
            "owner-role" => $"UPDATE bonbon.account_role SET status = 'Suspended' WHERE account_id = '{db.Owner}'",
            "customer-account" => $"UPDATE bonbon.account SET status = 'Inactive' WHERE account_id = '{db.Customer}'",
            "customer-role" => $"UPDATE bonbon.account_role SET status = 'Suspended' WHERE account_id = '{db.Customer}'",
            _ => "UPDATE bonbon.vehicle SET status = 'Suspended'",
        };
        await db.ExecuteAsync(sql);
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.Equal(error, (await service.ApproveAsync(db.Owner, db.FirstBooking)).ErrorCode);
        Assert.Equal("PendingApproval", await StatusAsync(db, db.FirstBooking));
        var rejection = await service.RejectAsync(db.Owner, db.FirstBooking);
        if (suspended.StartsWith("owner")) Assert.Equal("OWNER_INACTIVE", rejection.ErrorCode);
        else Assert.True(rejection.IsSuccess); // An active owner may clean up an ineligible request.
    }

    [LocalPostgresTheory]
    [InlineData("Confirmed")]
    [InlineData("ReadyForPickup")]
    [InlineData("InProgress")]
    [InlineData("Returned")]
    [InlineData("AwaitingSettlement")]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task Cannot_decide_a_booking_after_other_workflow_transitions(string status)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"UPDATE bonbon.booking SET status = '{status}' WHERE booking_id = '{db.FirstBooking}'");
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.Equal("BOOKING_STATE_CONFLICT", (await service.ApproveAsync(db.Owner, db.FirstBooking)).ErrorCode);
        Assert.Equal("BOOKING_STATE_CONFLICT", (await service.RejectAsync(db.Owner, db.FirstBooking)).ErrorCode);
        Assert.Equal(status, await StatusAsync(db, db.FirstBooking));
    }

    [LocalPostgresFact]
    public async Task Availability_block_prevents_approval_but_allows_rejection()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"""
            INSERT INTO bonbon.availability_block (vehicle_id, start_at, end_at)
            SELECT vehicle_id, start_at, end_at FROM bonbon.booking WHERE booking_id = '{db.FirstBooking}'
            """);
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.Equal("SCHEDULE_CONFLICT", (await service.ApproveAsync(db.Owner, db.FirstBooking)).ErrorCode);
        Assert.Equal("PendingApproval", await StatusAsync(db, db.FirstBooking));
        Assert.True((await service.RejectAsync(db.Owner, db.FirstBooking)).IsSuccess);
    }

    [LocalPostgresFact]
    public async Task Adjacent_intervals_can_both_be_approved()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync($"""
            UPDATE bonbon.booking SET start_at = start_at + interval '2 days', end_at = end_at + interval '2 days'
            WHERE booking_id = '{db.SecondBooking}'
            """);
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        Assert.True((await service.ApproveAsync(db.Owner, db.FirstBooking)).IsSuccess);
        Assert.True((await service.ApproveAsync(db.Owner, db.SecondBooking)).IsSuccess);
    }

    [LocalPostgresFact]
    public async Task Past_pickup_time_cannot_be_approved()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync("UPDATE bonbon.booking SET start_at = start_at - interval '20 days', end_at = end_at - interval '20 days'");
        await using var context = db.CreateContext();
        Assert.Equal("BOOKING_WINDOW_PASSED", (await new BookingService(new BookingRepository(context))
            .ApproveAsync(db.Owner, db.FirstBooking)).ErrorCode);
    }

    [LocalPostgresTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Concurrent_decisions_serialize_on_vehicle(bool sameBooking, bool rejectSecond)
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await using var gate = new NpgsqlConnection(db.ConnectionString);
        await gate.OpenAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT vehicle_id FROM bonbon.vehicle FOR UPDATE", gate, transaction))
            await command.ExecuteScalarAsync();

        await using var firstContext = db.CreateContext();
        await using var secondContext = db.CreateContext();
        var first = new BookingRepository(firstContext).DecideAsync(db.Owner, db.FirstBooking, BookingDecision.Approve);
        var second = new BookingRepository(secondContext).DecideAsync(db.Owner,
            sameBooking ? db.FirstBooking : db.SecondBooking, rejectSecond ? BookingDecision.Reject : BookingDecision.Approve);

        // Prove that both transactions have reached the vehicle lock, then release them together.
        var waiting = 0L;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            waiting = await db.ScalarAsync<long>("""
                SELECT count(*) FROM pg_stat_activity WHERE datname = current_database()
                    AND application_name = 'bonbon_approval_tests' AND wait_event_type = 'Lock'
                """);
            if (waiting >= 2) break;
            await Task.Delay(25);
        }
        await transaction.CommitAsync();
        var results = await Task.WhenAll(first, second);
        Assert.True(waiting >= 2, "Both decisions should wait for the vehicle before any booking lock.");
        Assert.Single(results, x => x.Outcome == BookingDecisionOutcome.Applied);
        var expected = !sameBooking ? BookingDecisionOutcome.ScheduleConflict
            : rejectSecond ? BookingDecisionOutcome.StateConflict : BookingDecisionOutcome.AlreadyApplied;
        Assert.Single(results, x => x.Outcome == expected);
        if (!sameBooking)
            Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM bonbon.booking WHERE status = 'AwaitingPayment'"));
    }

    [LocalPostgresFact]
    public async Task Existing_quote_create_and_mine_still_work()
    {
        await using var db = await DisposablePostgresDatabase.CreateAsync();
        await using var context = db.CreateContext();
        var service = new BookingService(new BookingRepository(context));
        var start = db.Start.AddDays(5);
        var quote = await service.QuoteAsync(new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = start, EndAt = start.AddDays(1) });
        Assert.True(quote.IsSuccess);
        var created = await service.CreateAsync(db.Customer, new() { VehicleId = db.Vehicle, RentalType = "Daily", StartAt = start, EndAt = start.AddDays(1) });
        Assert.True(created.IsSuccess);
        Assert.Equal("PendingApproval", created.Data!.Status);
        Assert.Contains((await service.GetMineAsync(db.Customer)).Data!, x => x.BookingId == created.Data.BookingId);
    }

    private static Task<string> StatusAsync(DisposablePostgresDatabase db, Guid bookingId) =>
        db.ScalarAsync<string>($"SELECT status FROM bonbon.booking WHERE booking_id = '{bookingId}'");
}
