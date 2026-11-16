using System.Data;
using BonBon.DataAccess.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BonBon.DataAccess.Repositories.Implementations;

public sealed class PaymentRepository(BonBonDbContext db, string provider = "VNPAY_SANDBOX") : IPaymentRepository
{
    private readonly string Provider = provider is "VNPAY_SANDBOX" or "SEPAY_SANDBOX" ? provider : throw new ArgumentException("Unsupported payment provider.");
    private string PaymentColumns => """
        p.payment_id, p.booking_id, p.provider_reference, p.amount, p.currency, p.status,
        p.created_at, p.expires_at, p.checkout_url, p.needs_reconciliation, p.provider_transaction_no, p.paid_at
        """ + (Provider == "SEPAY_SANDBOX" ? ", p.payment_purpose, p.additional_charge_id" : ", 'Rental'::text, NULL::uuid");

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
            var isSettlement = Provider == "SEPAY_SANDBOX" && booking.Status == "AwaitingSettlement";
            if (!isSettlement && (booking.Status != "AwaitingPayment" || booking.StartAt <= now || booking.HoldExpiresAt <= now || booking.VehicleStatus != "Available"))
                return new(PaymentCreateOutcome.InvalidBooking);
            var purpose = isSettlement ? "ExcessMileage" : "Rental";
            Guid? chargeId = null;
            var amount = booking.Amount;
            if (isSettlement)
            {
                await using var charge = Command("SELECT additional_charge_id, amount FROM bonbon.additional_charge WHERE booking_id=@booking AND status='Accepted' AND reason_code='EXCESS_MILEAGE' FOR UPDATE", connection, transaction, ("booking", bookingId));
                await using var reader = await charge.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return new(PaymentCreateOutcome.InvalidBooking);
                chargeId = reader.GetGuid(0); amount = reader.GetDecimal(1);
                if (await reader.ReadAsync(ct)) return new(PaymentCreateOutcome.InvalidBooking);
            }
            if (booking.Currency != "VND" || amount <= 0 || amount > 9999999999m || decimal.Truncate(amount) != amount)
                return new(PaymentCreateOutcome.InvalidAmount);

            var purposeFilter = Provider == "SEPAY_SANDBOX" ? " AND payment_purpose = @purpose" : "";
            await using (var paid = Command($"SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND status = 'Succeeded'{purposeFilter})",
                connection, transaction, ("booking", bookingId), ("purpose", purpose)))
                if ((bool)(await paid.ExecuteScalarAsync(ct))!) return new(PaymentCreateOutcome.AlreadyPaid);

            PaymentAttempt? previous;
            await using (var replay = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.booking_id = @booking AND p.provider = @provider AND p.idempotency_key = @key{purposeFilter} FOR UPDATE",
                connection, transaction, ("booking", bookingId), ("provider", Provider), ("key", idempotencyKey), ("purpose", purpose)))
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
            await using (var pending = Command($"SELECT {PaymentColumns} FROM bonbon.payment p WHERE p.booking_id = @booking AND p.provider = @provider AND (p.status IN ('Pending','Processing') OR p.needs_reconciliation){purposeFilter} ORDER BY p.created_at FOR UPDATE",
                connection, transaction, ("booking", bookingId), ("provider", Provider), ("purpose", purpose)))
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
            await using (var other = Command($"SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND provider <> @provider AND (status IN ('Pending','Processing') OR needs_reconciliation){purposeFilter})",
                connection, transaction, ("booking", bookingId), ("provider", Provider), ("purpose", purpose)))
                if ((bool)(await other.ExecuteScalarAsync(ct))!) return new(PaymentCreateOutcome.ReconciliationRequired);

            var id = Guid.NewGuid();
            var expires = isSettlement ? now.Add(lifetime) : now.Add(lifetime) < booking.StartAt ? now.Add(lifetime) : booking.StartAt;
            if (!isSettlement && booking.HoldExpiresAt is { } holdExpiry && holdExpiry < expires) expires = holdExpiry;
            var attempt = new PaymentAttempt(id, bookingId, id.ToString("N"), amount, booking.Currency, "Pending", now, expires, null, Purpose: purpose, AdditionalChargeId: chargeId);
            var url = buildCheckoutUrl(attempt); // Pure local signing; no external network call under locks.
            var purposeColumns = Provider == "SEPAY_SANDBOX" ? ", payment_purpose, additional_charge_id" : "";
            var purposeValues = Provider == "SEPAY_SANDBOX" ? ", @purpose, @charge" : "";
            await using var insert = Command($"""
                INSERT INTO bonbon.payment (payment_id, booking_id, provider_reference, provider, amount,
                    currency, status, created_at, expires_at, checkout_url, idempotency_key, updated_at{purposeColumns})
                VALUES (@id, @booking, @reference, @provider, @amount, 'VND', 'Pending', @now, @expires, @url, @key, @now{purposeValues})
                """, connection, transaction, ("id", id), ("booking", bookingId), ("reference", attempt.Reference),
                ("provider", Provider), ("amount", attempt.Amount), ("now", now), ("expires", expires), ("url", url), ("key", idempotencyKey), ("purpose", purpose), ("charge", chargeId));
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

            var active = await LockParticipantsAsync(connection, transaction, booking, ct) &&
                (payment.Purpose == "ExcessMileage" || booking.VehicleStatus == "Available");
            var dueAmount = booking.Amount;
            if (payment.Purpose == "ExcessMileage")
            {
                await using var charge = Command("SELECT amount FROM bonbon.additional_charge WHERE additional_charge_id=@charge AND booking_id=@booking AND reason_code='EXCESS_MILEAGE' AND status='Accepted' FOR UPDATE", connection, transaction,
                    ("charge", payment.AdditionalChargeId), ("booking", bookingId));
                var due = await charge.ExecuteScalarAsync(ct);
                dueAmount = due is decimal value ? value : 0;
            }
            bool otherSucceeded, transactionUsed;
            var purposeFilter = Provider == "SEPAY_SANDBOX" ? " AND payment_purpose = @purpose" : "";
            await using (var others = Command($"""
                SELECT EXISTS (SELECT 1 FROM bonbon.payment WHERE booking_id = @booking AND payment_id <> @id AND status = 'Succeeded'{purposeFilter}),
                    EXISTS (SELECT 1 FROM bonbon.payment WHERE provider = @provider AND provider_transaction_no = @txn AND payment_id <> @id)
                """, connection, transaction, ("booking", bookingId), ("id", payment.PaymentId),
                ("provider", Provider), ("txn", notification.TransactionNo), ("purpose", payment.Purpose)))
            {
                await using var reader = await others.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                otherSucceeded = reader.GetBoolean(0);
                transactionUsed = reader.GetBoolean(1);
            }
            var decision = decide(new(payment, booking.Status, dueAmount, booking.StartAt, active, otherSucceeded, transactionUsed, receivedAt));
            if (decision.Action == PaymentNotificationAction.InvalidReference) return PaymentNotificationOutcome.NotFound;
            if (decision.Action == PaymentNotificationAction.InvalidAmount) return PaymentNotificationOutcome.InvalidAmount;

            // Audit every distinct valid notification, including failures/out-of-order duplicates.
            var providerAuditColumns=Provider=="SEPAY_SANDBOX" ? ",gateway_provider" : "";
            var providerAuditValues=Provider=="SEPAY_SANDBOX" ? ",@provider" : "";
            await using (var audit = Command($"""
                INSERT INTO bonbon.payment_notification (payment_id, fingerprint, provider_transaction_no, amount_minor,
                    response_code, transaction_status, gateway_paid_at, received_at, disposition, reconciliation_reason{providerAuditColumns})
                VALUES (@id, @hash, @txn, @amount, @code, @status, @paid, @now, @action, @reason{providerAuditValues})
                """, connection, transaction, ("id", payment.PaymentId), ("hash", notification.Fingerprint), ("txn", notification.TransactionNo),
                ("amount", notification.AmountMinor), ("code", notification.ResponseCode), ("status", notification.TransactionStatus),
                ("paid", notification.PaidAt), ("now", receivedAt), ("action", decision.Action.ToString()), ("reason", decision.Reason)))
            {
                if(Provider=="SEPAY_SANDBOX") audit.Parameters.AddWithValue("provider",Provider);

                await audit.ExecuteNonQueryAsync(ct);
            }

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
                    if (payment.Purpose == "ExcessMileage")
                    {
                        await using var paidCharge = Command("UPDATE bonbon.additional_charge SET status='Paid' WHERE additional_charge_id=@charge AND booking_id=@id AND status='Accepted' AND amount=@amount", connection, transaction,
                            ("charge", payment.AdditionalChargeId), ("id", bookingId), ("amount", payment.Amount));
                        if (await paidCharge.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Locked additional charge changed.");
                        await using var finish = Command("UPDATE bonbon.booking SET status='Completed' WHERE booking_id=@id AND status='AwaitingSettlement' AND NOT EXISTS (SELECT 1 FROM bonbon.additional_charge WHERE booking_id=@id AND status IN ('Proposed','Accepted'))", connection, transaction, ("id", bookingId));
                        await finish.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        await using var confirm = Command("UPDATE bonbon.booking SET status = 'Confirmed' WHERE booking_id = @id AND status = 'AwaitingPayment'",
                            connection, transaction, ("id", bookingId));
                        if (await confirm.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Locked booking changed during payment confirmation.");
                    }
                }
                else if (decision.Action == PaymentNotificationAction.Failure && booking.HoldExpiresAt is not null)
                {
                    await using var release = Command("UPDATE bonbon.booking SET status = 'Cancelled' WHERE booking_id = @id AND status = 'AwaitingPayment' AND hold_expires_at IS NOT NULL",
                        connection, transaction, ("id", bookingId));
                    await release.ExecuteNonQueryAsync(ct);
                }
            }

            if (Provider=="SEPAY_SANDBOX") {
                await using var evidence=Command("INSERT INTO bonbon.sepay_payment_evidence(payment_id,fingerprint,evidence) VALUES(@id,@hash,@evidence)",
                    connection,transaction,("id",payment.PaymentId),("hash",notification.Fingerprint),
                    ("evidence",notification.SePayEvidence ?? throw new ArgumentException("Missing merchant-scoped SePay evidence.")));
                await evidence.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return decision.Action == PaymentNotificationAction.Duplicate ? PaymentNotificationOutcome.Duplicate : PaymentNotificationOutcome.Applied;
        }, ct);

    private sealed record LockedBooking(Guid BookingId, Guid CustomerId, Guid OwnerId, decimal Amount, string Currency, string Status, DateTimeOffset StartAt, string VehicleStatus, DateTimeOffset? HoldExpiresAt);

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
        await using var booking = Command("SELECT vehicle_id, customer_account_id, quoted_rental_amount, currency, status, start_at, hold_expires_at FROM bonbon.booking WHERE booking_id = @id FOR UPDATE",
            connection, transaction, ("id", id));
        await using var reader = await booking.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetGuid(0) != vehicleId) throw new PostgresException("Retry changed booking", "ERROR", "ERROR", "40001");
        return new(id, reader.GetGuid(1), owner, reader.GetDecimal(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5), vehicleStatus,
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
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
        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetGuid(13));

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
                    AND (SELECT count(*) = 9 FROM information_schema.columns WHERE table_schema = 'bonbon' AND table_name = 'payment'
                        AND column_name IN ('idempotency_key','checkout_url','expires_at','provider_transaction_no','response_code',
                            'transaction_status','updated_at','needs_reconciliation','reconciliation_reason'))
                """, connection, null))
                if (!(bool)(await guard.ExecuteScalarAsync(ct))!) throw new PaymentSchemaUnavailableException();
            await using (var version = Command("SELECT EXISTS (SELECT 1 FROM bonbon.payment_gateway_schema WHERE version = 1)", connection, null))
                if (!(bool)(await version.ExecuteScalarAsync(ct))!) throw new PaymentSchemaUnavailableException();

            if (Provider == "VNPAY_SANDBOX") {
                await using var legacyGuard = Command("""
                    SELECT (SELECT count(*) = 4 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                        JOIN pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
                          AND c.relname IN ('uq_vnpay_sandbox_active', 'uq_vnpay_sandbox_idempotency',
                            'uq_vnpay_sandbox_accepted_success', 'uq_vnpay_sandbox_transaction'))
                    """, connection, null);
                if (await legacyGuard.ExecuteScalarAsync(ct) is not true) throw new PaymentSchemaUnavailableException();
            }
            if(Provider=="SEPAY_SANDBOX") {
                await using var guard=Command("SELECT to_regclass('bonbon.sepay_gateway_schema') IS NOT NULL AND to_regclass('bonbon.sepay_payment_evidence') IS NOT NULL AND to_regclass('bonbon.sepay_rejected_notification') IS NOT NULL",connection,null);
                if(await guard.ExecuteScalarAsync(ct) is not true) throw new PaymentSchemaUnavailableException();
                await using var returnSchema = Command("SELECT count(*)=2 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='payment' AND column_name IN ('payment_purpose','additional_charge_id')", connection, null);
                if (await returnSchema.ExecuteScalarAsync(ct) is not true) throw new PaymentSchemaUnavailableException();
                await using var returnIndexes = Command("""
                    SELECT count(*)=4 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
                      AND c.relname IN ('uq_sepay_sandbox_active','uq_sepay_sandbox_accepted_success','uq_sandbox_active_booking','uq_sandbox_accepted_booking')
                      AND pg_get_indexdef(i.indexrelid) LIKE '%(booking_id, payment_purpose)%'
                    """, connection, null);
                if (await returnIndexes.ExecuteScalarAsync(ct) is not true) throw new PaymentSchemaUnavailableException();
                await using var marker=Command("SELECT EXISTS(SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1) AND (SELECT count(*)=4 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready AND c.relname IN ('uq_sepay_sandbox_idempotency','uq_sepay_sandbox_active','uq_sepay_sandbox_accepted_success','uq_sepay_sandbox_transaction'))",connection,null);
                if(await marker.ExecuteScalarAsync(ct) is not true) throw new PaymentSchemaUnavailableException();
            }
            return await action(connection);
        }
        finally { if (close) await connection.CloseAsync(); }
    }
}
