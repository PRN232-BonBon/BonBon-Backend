-- Instant bookings reserve the vehicle while waiting for payment. Existing
-- historical bookings are intentionally not converted or cancelled here.
BEGIN;

ALTER TABLE bonbon.booking ADD COLUMN IF NOT EXISTS hold_expires_at timestamptz;

CREATE OR REPLACE FUNCTION bonbon.check_vehicle_schedule()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
BEGIN
    IF current_setting('transaction_isolation') = 'repeatable read' THEN
        RAISE EXCEPTION 'Schedule writes require Read Committed or Serializable' USING ERRCODE = '25000';
    END IF;
    PERFORM 1 FROM bonbon.vehicle WHERE vehicle_id = NEW.vehicle_id FOR UPDATE;
    IF TG_TABLE_NAME = 'booking' THEN
        IF NEW.status NOT IN ('Rejected', 'Cancelled') AND EXISTS (
            SELECT 1 FROM bonbon.availability_block ab WHERE ab.vehicle_id = NEW.vehicle_id
              AND tstzrange(ab.start_at, ab.end_at, '[)') && tstzrange(NEW.start_at, NEW.end_at, '[)')) THEN
            RAISE EXCEPTION 'Booking overlaps an availability block' USING ERRCODE = '23P01';
        END IF;
    ELSE
        IF EXISTS (
            SELECT 1 FROM bonbon.booking b WHERE b.vehicle_id = NEW.vehicle_id
              AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
                               'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
              AND tstzrange(b.start_at, b.end_at, '[)') && tstzrange(NEW.start_at, NEW.end_at, '[)')) THEN
            RAISE EXCEPTION 'Availability block overlaps a reserved booking' USING ERRCODE = '23P01';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

COMMIT;
