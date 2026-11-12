using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed partial class BookingRepository
{
    public Task<BookingDecisionResult> DecideAsync(
        Guid ownerAccountId, Guid bookingId, BookingDecision decision, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        return WithConnectionAsync<BookingDecisionResult>(async connection =>
        {
            // A new snapshot after waiting for the vehicle lock must see the winner's commit.
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using (var timeouts = new NpgsqlCommand(
                "SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '15s'", connection, transaction))
                await timeouts.ExecuteNonQueryAsync(ct);

            await using var locate = new NpgsqlCommand(
                "SELECT vehicle_id FROM bonbon.booking WHERE booking_id = @booking_id", connection, transaction);
            locate.Parameters.AddWithValue("booking_id", bookingId);
            var vehicleIdValue = await locate.ExecuteScalarAsync(ct);
            if (vehicleIdValue is not Guid vehicleId)
                return new(BookingDecisionOutcome.NotFound);

            // The first row lock is always Vehicle, never Booking (including rejection/replays).
            Guid actualOwner;
            string vehicleStatus;
            await using (var vehicle = new NpgsqlCommand(
                "SELECT owner_account_id, status FROM bonbon.vehicle WHERE vehicle_id = @vehicle_id FOR UPDATE",
                connection, transaction))
            {
                vehicle.Parameters.AddWithValue("vehicle_id", vehicleId);
                await using var reader = await vehicle.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return new(BookingDecisionOutcome.NotFound);
                actualOwner = reader.GetGuid(0);
                vehicleStatus = reader.GetString(1);
            }

            Guid customerId;
            DateTimeOffset startAt;
            DateTimeOffset endAt;
            BookingDecisionSnapshot snapshot;
            await using (var booking = new NpgsqlCommand("""
                SELECT vehicle_id, customer_account_id, start_at, end_at, status,
                       unit_price_snapshot, quoted_rental_amount, currency
                FROM bonbon.booking WHERE booking_id = @booking_id FOR UPDATE
                """, connection, transaction))
            {
                booking.Parameters.AddWithValue("booking_id", bookingId);
                await using var reader = await booking.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return new(BookingDecisionOutcome.NotFound);
                // Older schemas may permit moving a pending booking. Never lock a second vehicle
                // out of order; roll back and ask the caller to retry instead.
                if (reader.GetGuid(0) != vehicleId) return new(BookingDecisionOutcome.ConcurrentChange);
                customerId = reader.GetGuid(1);
                startAt = reader.GetFieldValue<DateTimeOffset>(2);
                endAt = reader.GetFieldValue<DateTimeOffset>(3);
                snapshot = new(bookingId, reader.GetString(4), reader.GetDecimal(5),
                    reader.GetDecimal(6), reader.GetString(7));
            }

            if (actualOwner != ownerAccountId) return new(BookingDecisionOutcome.NotOwner);

            // SHARE (not KEY SHARE) also blocks concurrent status changes until commit.
            // Stable ordering handles accounts that hold both rental roles.
            bool ownerActive = false, customerActive = false;
            await using (var actors = new NpgsqlCommand("""
                SELECT a.account_id, a.status, ar.role_id, ar.status
                FROM bonbon.account a JOIN bonbon.account_role ar USING (account_id)
                WHERE (a.account_id = @owner AND ar.role_id = 3)
                   OR (a.account_id = @customer AND ar.role_id = 2)
                ORDER BY a.account_id, ar.role_id
                FOR SHARE OF a, ar
                """, connection, transaction))
            {
                actors.Parameters.AddWithValue("owner", ownerAccountId);
                actors.Parameters.AddWithValue("customer", customerId);
                await using var reader = await actors.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var active = reader.GetString(1) == "Active" && reader.GetString(3) == "Active";
                    if (reader.GetGuid(0) == ownerAccountId && reader.GetInt16(2) == 3) ownerActive = active;
                    if (reader.GetGuid(0) == customerId && reader.GetInt16(2) == 2) customerActive = active;
                }
            }
            if (!ownerActive) return new(BookingDecisionOutcome.OwnerInactive);

            var target = decision == BookingDecision.Approve ? "AwaitingPayment" : "Rejected";
            if (snapshot.Status == target)
            {
                await transaction.CommitAsync(ct);
                return new(BookingDecisionOutcome.AlreadyApplied, snapshot);
            }
            if (snapshot.Status != "PendingApproval") return new(BookingDecisionOutcome.StateConflict);

            if (decision == BookingDecision.Approve)
            {
                if (!customerActive) return new(BookingDecisionOutcome.CustomerInactive);
                if (vehicleStatus != "Available") return new(BookingDecisionOutcome.VehicleUnavailable);
                await using var schedule = new NpgsqlCommand("""
                    SELECT start_at <= clock_timestamp(),
                        EXISTS (
                            SELECT 1 FROM bonbon.availability_block ab
                            WHERE ab.vehicle_id = @vehicle_id
                              AND tstzrange(ab.start_at, ab.end_at, '[)') && tstzrange(@start_at, @end_at, '[)')
                        ) OR EXISTS (
                            SELECT 1 FROM bonbon.booking b
                            WHERE b.vehicle_id = @vehicle_id AND b.booking_id <> @booking_id
                              AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
                                  'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
                              AND tstzrange(b.start_at, b.end_at, '[)') && tstzrange(@start_at, @end_at, '[)')
                        )
                    FROM bonbon.booking WHERE booking_id = @booking_id
                    """, connection, transaction);
                schedule.Parameters.AddWithValue("vehicle_id", vehicleId);
                schedule.Parameters.AddWithValue("booking_id", bookingId);
                schedule.Parameters.AddWithValue("start_at", startAt);
                schedule.Parameters.AddWithValue("end_at", endAt);
                await using var reader = await schedule.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                if (reader.GetBoolean(0)) return new(BookingDecisionOutcome.BookingWindowPassed);
                if (reader.GetBoolean(1)) return new(BookingDecisionOutcome.ScheduleConflict);
            }

            // Price, generated total, participants and rental interval are deliberately untouched.
            await using var update = new NpgsqlCommand("""
                UPDATE bonbon.booking SET status = @status
                WHERE booking_id = @booking_id AND status = 'PendingApproval'
                """, connection, transaction);
            update.Parameters.AddWithValue("status", target);
            update.Parameters.AddWithValue("booking_id", bookingId);
            if (await update.ExecuteNonQueryAsync(ct) != 1) return new(BookingDecisionOutcome.StateConflict);
            await transaction.CommitAsync(ct);
            return new(BookingDecisionOutcome.Applied, snapshot with { Status = target });
            // Every non-committed return/exception disposes and rolls back the transaction.
        }, ct);
    }
}
