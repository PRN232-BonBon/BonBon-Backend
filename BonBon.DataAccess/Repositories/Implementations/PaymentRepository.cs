using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class PaymentRepository(BonBonDbContext db) : IPaymentRepository
{
    private const string Provider = "VNPAY_SANDBOX";
    private const string PaymentColumns = """
        p.payment_id, p.booking_id, p.provider_reference, p.amount, p.currency, p.status,
        p.created_at, p.expires_at, p.checkout_url, p.needs_reconciliation, p.provider_transaction_no, p.paid_at
        """;

    public Task<PaymentCreateResult> CreateAsync(Guid customerId, Guid bookingId, string idempotencyKey,
        DateTimeOffset now, TimeSpan lifetime, Func<PaymentAttempt, string> buildCheckoutUrl, CancellationToken ct = default) =>
        WithConnectionAsync<PaymentCreateResult>(async connection =>
        {
            await using var transaction = await BeginAsync(connection, ct);
            var booking = await LockBookingAsync(connection, transaction, bookingId, ct);
            if (booking is null) return new(PaymentCreateOutcome.NotFound);
            if (booking.CustomerId != customerId) return new(PaymentCreateOutcome.Forbidden);
            var active = await LockParticipantsAsync(connection, transaction, booking, ct);
            if (!active) return new(PaymentCreateOutcome.Inactive);
            if (booking.Status != "AwaitingPayment" || booking.StartAt <= now || booking.VehicleStatus != "Available")
                return new(PaymentCreateOutcome.InvalidBooking);
            if (booking.Currency != "VND" || booking.Amount <= 0 || booking.Amount > 9999999999m || decimal.Truncate(booking.Amount) != booking.Amount)
                return new(PaymentCreateOutcome.InvalidAmount);

            await using (var paid = Command("SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND status = 'Succeeded')",
                connection, transaction, ("booking", bookingId)))
                if ((bool)(await paid.ExecuteScalarAsync(ct))!) return new(PaymentCreateOutcome.AlreadyPaid);

            PaymentAttempt? previous;
            await using (var replay = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.booking_id = @booking AND p.provider = @provider AND p.idempotency_key = @key FOR UPDATE",
                connection, transaction, ("booking", bookingId), ("provider", Provider), ("key", idempotencyKey)))
            {
                await using var reader = await replay.ExecuteReaderAsync(ct);
                previous = await reader.ReadAsync(ct) ? ReadPayment(reader) : null;
            }
            if (previous is not null)
            {
                if (previous.NeedsReconciliation || (previous.Status is "Pending" or "Processing" && previous.ExpiresAt <= now))
                {
                    await FlagExpiredAsync(connection, transaction, previous, now, ct);
                    await transaction.CommitAsync(ct);
                    return new(PaymentCreateOutcome.ReconciliationRequired);
                }
                await transaction.CommitAsync(ct);
                return new(PaymentCreateOutcome.Reused, previous);
            }
            PaymentAttempt? open;
            await using (var pending = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.booking_id = @booking AND p.provider = @provider AND (p.status IN ('Pending','Processing') OR p.needs_reconciliation) ORDER BY p.created_at FOR UPDATE",
                connection, transaction, ("booking", bookingId), ("provider", Provider)))
            {
                await using var reader = await pending.ExecuteReaderAsync(ct);
                open = await reader.ReadAsync(ct) ? ReadPayment(reader) : null;
            }
            if (open is not null)
            {
                if (open.NeedsReconciliation || open.ExpiresAt <= now)
                {
                    await FlagExpiredAsync(connection, transaction, open, now, ct);
                    await transaction.CommitAsync(ct);
                    return new(PaymentCreateOutcome.ReconciliationRequired);
                }
                return new(PaymentCreateOutcome.InProgress);
            }
            // Do not replace attempts owned by a different provider or legacy integration.
            await using (var other = Command("SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND provider <> @provider AND status IN ('Pending','Processing'))",
                connection, transaction, ("booking", bookingId), ("provider", Provider)))
                if ((bool)(await other.ExecuteScalarAsync(ct))!) return new(PaymentCreateOutcome.ReconciliationRequired);

            var id = Guid.NewGuid();
            var expires = now.Add(lifetime) < booking.StartAt ? now.Add(lifetime) : booking.StartAt;
            var attempt = new PaymentAttempt(id, bookingId, id.ToString("N"), booking.Amount, booking.Currency, "Pending", now, expires, null);
            var url = buildCheckoutUrl(attempt); // Pure local signing; no external network call under locks.
            await using var insert = Command("""
                INSERT INTO bonbon.payment (payment_id, booking_id, provider_reference, provider, amount,
                    currency, status, created_at, expires_at, checkout_url, idempotency_key, updated_at)
                VALUES (@id, @booking, @reference, @provider, @amount, 'VND', 'Pending', @now, @expires, @url, @key, @now)
                """, connection, transaction, ("id", id), ("booking", bookingId), ("reference", attempt.Reference),
                ("provider", Provider), ("amount", attempt.Amount), ("now", now), ("expires", expires), ("url", url), ("key", idempotencyKey));
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return new(PaymentCreateOutcome.Created, attempt with { CheckoutUrl = url });
        }, ct);

    public Task<PaymentAttempt?> GetForCustomerAsync(Guid customerId, Guid paymentId, CancellationToken ct = default) =>
        WithConnectionAsync<PaymentAttempt?>(async connection =>
        {
            await using var command = Command($"""
                SELECT {PaymentColumns} FROM bonbon.payment p JOIN bonbon.booking b USING (booking_id)
                WHERE p.payment_id = @id AND p.provider = @provider AND b.customer_account_id = @customer
                  AND bonbon.has_active_role(@customer, 2::smallint)
                """, connection, null, ("id", paymentId), ("provider", Provider), ("customer", customerId));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadPayment(reader) : null;
        }, ct);

    public Task<PaymentAttempt?> GetByReferenceAsync(string reference, CancellationToken ct = default) =>
        WithConnectionAsync<PaymentAttempt?>(async connection =>
        {
            await using var command = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.provider_reference = @reference AND p.provider = @provider",
                connection, null, ("reference", reference), ("provider", Provider));
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadPayment(reader) : null;
        }, ct);

    public Task<PaymentNotificationOutcome> ProcessNotificationAsync(VerifiedPaymentNotification notification,
        DateTimeOffset receivedAt, Func<PaymentNotificationContext, PaymentNotificationDecision> decide, CancellationToken ct = default) =>
        WithConnectionAsync<PaymentNotificationOutcome>(async connection =>
        {
            await using var transaction = await BeginAsync(connection, ct);
            Guid bookingId;
            await using (var locate = Command("SELECT booking_id FROM bonbon.payment WHERE provider_reference = @reference AND provider = @provider",
                connection, transaction, ("reference", notification.Reference), ("provider", Provider)))
            {
                if (await locate.ExecuteScalarAsync(ct) is not Guid id) return PaymentNotificationOutcome.NotFound;
                bookingId = id;
            }
            var booking = await LockBookingAsync(connection, transaction, bookingId, ct);
            if (booking is null) return PaymentNotificationOutcome.NotFound;
            PaymentAttempt payment;
            await using (var locked = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.provider_reference = @reference AND p.booking_id = @booking AND p.provider = @provider FOR UPDATE",
                connection, transaction, ("reference", notification.Reference), ("booking", bookingId), ("provider", Provider)))
            {
                await using var reader = await locked.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return PaymentNotificationOutcome.NotFound;
                payment = ReadPayment(reader);
            }
            await using (var duplicate = Command("SELECT EXISTS (SELECT 1 FROM bonbon.payment_notification WHERE payment_id = @id AND fingerprint = @hash)",
                connection, transaction, ("id", payment.PaymentId), ("hash", notification.Fingerprint)))
                if ((bool)(await duplicate.ExecuteScalarAsync(ct))!) return PaymentNotificationOutcome.Duplicate;

            var active = await LockParticipantsAsync(connection, transaction, booking, ct) && booking.VehicleStatus == "Available";
            bool otherSucceeded, transactionUsed;
            await using (var others = Command("""
                SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND payment_id <> @id AND status = 'Succeeded'),
                    EXISTS (SELECT 1 FROM bonbon.payment WHERE provider = @provider AND provider_transaction_no = @txn AND payment_id <> @id)
                """, connection, transaction, ("booking", bookingId), ("id", payment.PaymentId),
                ("provider", Provider), ("txn", notification.TransactionNo)))
            {
                await using var reader = await others.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                otherSucceeded = reader.GetBoolean(0);
                transactionUsed = reader.GetBoolean(1);
            }
            var decision = decide(new(payment, booking.Status, booking.Amount, booking.StartAt, active, otherSucceeded, transactionUsed, receivedAt));
            if (decision.Action == PaymentNotificationAction.InvalidReference) return PaymentNotificationOutcome.NotFound;
            if (decision.Action == PaymentNotificationAction.InvalidAmount) return PaymentNotificationOutcome.InvalidAmount;

            // Audit every distinct valid notification, including failures/out-of-order duplicates.
            await using (var audit = Command("""
                INSERT INTO bonbon.payment_notification (payment_id, fingerprint, provider_transaction_no, amount_minor,
                    response_code, transaction_status, gateway_paid_at, received_at, disposition, reconciliation_reason)
                VALUES (@id, @hash, @txn, @amount, @code, @status, @paid, @now, @action, @reason)
                """, connection, transaction, ("id", payment.PaymentId), ("hash", notification.Fingerprint), ("txn", notification.TransactionNo),
                ("amount", notification.AmountMinor), ("code", notification.ResponseCode), ("status", notification.TransactionStatus),
                ("paid", notification.PaidAt), ("now", receivedAt), ("action", decision.Action.ToString()), ("reason", decision.Reason)))
                await audit.ExecuteNonQueryAsync(ct);

            if (decision.Action != PaymentNotificationAction.Duplicate)
            {
                var success = decision.Action is PaymentNotificationAction.Success or PaymentNotificationAction.ReconcileSuccess;
                var reconcile = decision.Action is PaymentNotificationAction.ReconcileSuccess or PaymentNotificationAction.ReconcilePending;
                var status = success || payment.Status == "Succeeded" ? "Succeeded"
                    : decision.Action == PaymentNotificationAction.Failure ? (notification.ResponseCode == "24" ? "Cancelled" : "Failed")
                    : payment.Status is "Failed" or "Cancelled" ? payment.Status : "Processing";
                await using var update = Command("""
                    UPDATE bonbon.payment SET status = @status, paid_at = coalesce(paid_at, @paid),
                        provider_transaction_no = coalesce(provider_transaction_no, @txn), response_code = @code,
                        transaction_status = @gateway_status, needs_reconciliation = @reconcile,
                        reconciliation_reason = @reason, updated_at = @now WHERE payment_id = @id
                    """, connection, transaction, ("status", status), ("paid", success ? notification.PaidAt : null),
                    ("txn", success && !transactionUsed ? notification.TransactionNo : null), ("code", notification.ResponseCode),
                    ("gateway_status", notification.TransactionStatus), ("reconcile", reconcile), ("reason", decision.Reason),
                    ("now", receivedAt), ("id", payment.PaymentId));
                await update.ExecuteNonQueryAsync(ct);
                if (decision.Action == PaymentNotificationAction.Success)
                {
                    await using var confirm = Command("UPDATE bonbon.booking SET status = 'Confirmed' WHERE booking_id = @id AND status = 'AwaitingPayment'",
                        connection, transaction, ("id", bookingId));
                    if (await confirm.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Locked booking changed during payment confirmation.");
                }
            }
            await transaction.CommitAsync(ct);
            return decision.Action == PaymentNotificationAction.Duplicate ? PaymentNotificationOutcome.Duplicate : PaymentNotificationOutcome.Applied;
        }, ct);

    private sealed record LockedBooking(Guid BookingId, Guid CustomerId, Guid OwnerId, decimal Amount, string Currency, string Status, DateTimeOffset StartAt, string VehicleStatus);

    private static async Task<LockedBooking?> LockBookingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        Guid vehicleId;
        await using (var locate = Command("SELECT vehicle_id FROM bonbon.booking WHERE booking_id = @id", connection, transaction, ("id", id)))
        {
            if (await locate.ExecuteScalarAsync(ct) is not Guid found) return null;
            vehicleId = found;
        }
        Guid owner;
        string vehicleStatus;
        await using (var vehicle = Command("SELECT owner_account_id, status FROM bonbon.vehicle WHERE vehicle_id = @id FOR UPDATE", connection, transaction, ("id", vehicleId)))
        {
            await using var vehicleReader = await vehicle.ExecuteReaderAsync(ct);
            if (!await vehicleReader.ReadAsync(ct)) return null;
            owner = vehicleReader.GetGuid(0);
            vehicleStatus = vehicleReader.GetString(1);
        }
        await using var booking = Command("SELECT vehicle_id, customer_account_id, quoted_rental_amount, currency, status, start_at FROM bonbon.booking WHERE booking_id = @id FOR UPDATE",
            connection, transaction, ("id", id));
        await using var reader = await booking.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetGuid(0) != vehicleId) throw new PostgresException("Retry changed booking", "ERROR", "ERROR", "40001");
        return new(id, reader.GetGuid(1), owner, reader.GetDecimal(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), vehicleStatus);
    }

    private static async Task<bool> LockParticipantsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LockedBooking booking, CancellationToken ct)
    {
        await using var actors = Command("""
            SELECT a.account_id, ar.role_id, a.status = 'Active' AND ar.status = 'Active'
            FROM bonbon.account a JOIN bonbon.account_role ar USING (account_id)
            WHERE (a.account_id = @customer AND ar.role_id = 2) OR (a.account_id = @owner AND ar.role_id = 3)
            ORDER BY a.account_id, ar.role_id FOR SHARE OF a, ar
            """, connection, transaction, ("customer", booking.CustomerId), ("owner", booking.OwnerId));
        var customerActive = false;
        var ownerActive = false;
        await using var reader = await actors.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.GetInt16(1) == 2 && reader.GetGuid(0) == booking.CustomerId) customerActive = reader.GetBoolean(2);
            if (reader.GetInt16(1) == 3 && reader.GetGuid(0) == booking.OwnerId) ownerActive = reader.GetBoolean(2);
        }
        return customerActive && ownerActive;
    }

    private static async Task FlagExpiredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PaymentAttempt payment, DateTimeOffset now, CancellationToken ct)
    {
        if (payment.NeedsReconciliation) return;
        await using var command = Command("""
            UPDATE bonbon.payment SET needs_reconciliation = true, reconciliation_reason = 'EXPIRED_AWAITING_GATEWAY', updated_at = @now
            WHERE payment_id = @id
            """, connection, transaction, ("id", payment.PaymentId), ("now", now));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static PaymentAttempt ReadPayment(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
        reader.GetDecimal(3), reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetBoolean(9), reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11));

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection, NpgsqlTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await using var timeouts = Command("SET LOCAL lock_timeout = '3s'; SET LOCAL statement_timeout = '10s'", connection, transaction);
            await timeouts.ExecuteNonQueryAsync(ct);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    private async Task<T> WithConnectionAsync<T>(Func<NpgsqlConnection, Task<T>> action, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            // Fail closed on the current shared schema; never bootstrap or migrate at runtime.
            await using (var guard = Command("""
                SELECT to_regclass('bonbon.payment_gateway_schema') IS NOT NULL
                    AND to_regclass('bonbon.payment_notification') IS NOT NULL
                    AND to_regclass('bonbon.uq_vnpay_sandbox_active') IS NOT NULL
                    AND to_regclass('bonbon.uq_vnpay_sandbox_idempotency') IS NOT NULL
                    AND to_regclass('bonbon.uq_vnpay_sandbox_accepted_success') IS NOT NULL
                    AND to_regclass('bonbon.uq_vnpay_sandbox_transaction') IS NOT NULL
                    AND (SELECT count(*) = 4 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
                          AND c.relname IN ('uq_vnpay_sandbox_active', 'uq_vnpay_sandbox_idempotency',
                            'uq_vnpay_sandbox_accepted_success', 'uq_vnpay_sandbox_transaction'))
                    AND (SELECT count(*) = 9 FROM information_schema.columns WHERE table_schema = 'bonbon' AND table_name = 'payment'
                        AND column_name IN ('idempotency_key','checkout_url','expires_at','provider_transaction_no','response_code',
                            'transaction_status','updated_at','needs_reconciliation','reconciliation_reason'))
                """, connection, null))
                if (!(bool)(await guard.ExecuteScalarAsync(ct))!) throw new PaymentSchemaUnavailableException();
            await using (var version = Command("SELECT EXISTS (SELECT 1 FROM bonbon.payment_gateway_schema WHERE version = 1)", connection, null))
                if (!(bool)(await version.ExecuteScalarAsync(ct))!) throw new PaymentSchemaUnavailableException();
            return await action(connection);
        }
        finally { if (close) await connection.CloseAsync(); }
    }
}
