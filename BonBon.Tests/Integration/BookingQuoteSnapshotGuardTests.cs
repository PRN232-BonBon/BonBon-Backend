using BonBon.Business.Services.Implementations;
using BonBon.DataAccess.Repositories.Implementations;
using Npgsql;

namespace BonBon.Tests.Integration;

public class BookingQuoteSnapshotGuardTests
{
    // Dedicated loopback disposable PostgreSQL ONLY. Skipped in this review;
    // never point the disposable fixture at a shared/staging/production server.
    [LocalPostgresFact]
    public async Task Proposal_005_locks_discount_and_terms_from_006_at_and_after_decision()
    {
        await using var db=await DisposablePostgresDatabase.CreateAsync();
        await db.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"Sql","005_booking_quote_guard.sql")));
        var atomic=await Assert.ThrowsAsync<PostgresException>(()=>db.ExecuteAsync(
            $"UPDATE bonbon.booking SET discount_amount_snapshot=100,status='AwaitingPayment' WHERE booking_id='{db.FirstBooking}'"));
        Assert.Equal("23514",atomic.SqlState);
        await using var context=db.CreateContext();
        Assert.True((await new BookingService(new BookingRepository(context)).ApproveAsync(db.Owner,db.FirstBooking)).IsSuccess);
        foreach(var change in new[]{"discount_amount_snapshot=100","weekly_discount_percent_snapshot=5",
            "included_mileage_km_snapshot=200,overage_fee_per_km_snapshot=2000","rental_terms_snapshot='edited test terms'"})
        {
            var error=await Assert.ThrowsAsync<PostgresException>(()=>db.ExecuteAsync($"UPDATE bonbon.booking SET {change} WHERE booking_id='{db.FirstBooking}'"));
            Assert.Equal("23514",error.SqlState);
        }
        Assert.Equal(1300000m,await db.ScalarAsync<decimal>($"SELECT quoted_rental_amount FROM bonbon.booking WHERE booking_id='{db.FirstBooking}'"));
    }
}
