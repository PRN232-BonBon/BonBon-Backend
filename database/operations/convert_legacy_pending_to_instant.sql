-- Operator-run cutover only, after reviewing the pending bookings, backup, and
-- deployment order. Not applied at startup. Rows that are ineligible or
-- conflict are intentionally left PendingApproval for manual review.
-- Existing AwaitingPayment bookings and payment attempts are never changed.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
LOCK TABLE bonbon.booking, bonbon.availability_block IN SHARE ROW EXCLUSIVE MODE;

DO $$
DECLARE
    candidate record;
    converted integer := 0;
    skipped integer := 0;
BEGIN
    -- Stop changes to vehicles before deciding whether their legacy bookings
    -- are eligible. Table-level booking locks serialize schedule writes.
    PERFORM v.vehicle_id FROM bonbon.vehicle v
      WHERE EXISTS (SELECT 1 FROM bonbon.booking b WHERE b.vehicle_id=v.vehicle_id AND b.status='PendingApproval')
      ORDER BY v.vehicle_id FOR UPDATE;

    FOR candidate IN
        SELECT b.booking_id, b.vehicle_id, b.customer_account_id, b.start_at, b.end_at,
               v.owner_account_id, v.status AS vehicle_status
        FROM bonbon.booking b JOIN bonbon.vehicle v ON v.vehicle_id=b.vehicle_id
        WHERE b.status='PendingApproval'
        ORDER BY b.created_at, b.booking_id
    LOOP
        IF candidate.start_at <= clock_timestamp()
           OR candidate.vehicle_status <> 'Available'
           OR NOT bonbon.has_active_role(candidate.customer_account_id, 2::smallint)
           OR NOT bonbon.has_active_role(candidate.owner_account_id, 3::smallint)
           OR EXISTS (
               SELECT 1 FROM bonbon.availability_block ab
               WHERE ab.vehicle_id=candidate.vehicle_id
                 AND tstzrange(ab.start_at, ab.end_at, '[)') && tstzrange(candidate.start_at, candidate.end_at, '[)')
           ) OR EXISTS (
               SELECT 1 FROM bonbon.booking b
               WHERE b.vehicle_id=candidate.vehicle_id AND b.booking_id<>candidate.booking_id
                 AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup', 'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
                 AND tstzrange(b.start_at, b.end_at, '[)') && tstzrange(candidate.start_at, candidate.end_at, '[)')
           ) THEN
            skipped := skipped + 1;
            CONTINUE;
        END IF;

        UPDATE bonbon.booking SET status='AwaitingPayment',
            hold_expires_at=LEAST(clock_timestamp()+interval '15 minutes', candidate.start_at)
        WHERE booking_id=candidate.booking_id AND status='PendingApproval';
        converted := converted + 1;
    END LOOP;
    RAISE NOTICE 'Legacy bookings converted: %, left pending for review: %', converted, skipped;
END $$;
COMMIT;
