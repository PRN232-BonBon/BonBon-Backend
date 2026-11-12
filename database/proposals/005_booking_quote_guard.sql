-- PROPOSAL. Test database only until separately approved for deployment.
-- Never included in application startup. Preserve existing owner, ACL and trigger.
BEGIN;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '15s';
DO $$ BEGIN
    IF to_regprocedure('bonbon.protect_booking_quote()') IS NULL OR NOT EXISTS (
        SELECT 1 FROM pg_trigger WHERE tgrelid='bonbon.booking'::regclass
          AND tgname='booking_quote_guard' AND NOT tgisinternal AND tgenabled IN ('O','A')
          AND tgfoid='bonbon.protect_booking_quote()'::regprocedure
    ) THEN RAISE EXCEPTION 'Expected enabled Booking quote guard is required'; END IF;
END $$;
CREATE OR REPLACE FUNCTION bonbon.protect_booking_quote()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
BEGIN
    IF NEW.customer_account_id IS DISTINCT FROM OLD.customer_account_id
       OR NEW.vehicle_id IS DISTINCT FROM OLD.vehicle_id THEN
        RAISE EXCEPTION 'Booking participants cannot be replaced' USING ERRCODE='23514';
    END IF;
    IF OLD.status <> 'PendingApproval' AND NEW.status='PendingApproval' THEN
        RAISE EXCEPTION 'Booking cannot return to PendingApproval after a decision' USING ERRCODE='23514';
    END IF;
    IF (OLD.status <> 'PendingApproval' OR NEW.status <> 'PendingApproval') AND (
        NEW.rental_type IS DISTINCT FROM OLD.rental_type
        OR NEW.billable_units IS DISTINCT FROM OLD.billable_units
        OR NEW.start_at IS DISTINCT FROM OLD.start_at
        OR NEW.end_at IS DISTINCT FROM OLD.end_at
        OR NEW.unit_price_snapshot IS DISTINCT FROM OLD.unit_price_snapshot) THEN
        RAISE EXCEPTION 'Decided booking time and price are immutable' USING ERRCODE='23514';
    END IF;
    RETURN NEW;
END;
$$;
COMMIT;
