-- Apply only after backing up and reviewing the target. Historical payments remain Rental.
-- Deploy this migration before enabling code that can collect return surcharges.
BEGIN;
SET LOCAL lock_timeout='5s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF to_regclass('bonbon.sepay_gateway_schema') IS NULL OR
    NOT EXISTS (SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1) THEN
   RAISE EXCEPTION 'SePay baseline is required';
 END IF;
END $$;
ALTER TABLE bonbon.payment ADD COLUMN payment_purpose varchar(20) NOT NULL DEFAULT 'Rental'
    CHECK(payment_purpose IN ('Rental','ExcessMileage'));
ALTER TABLE bonbon.payment ADD COLUMN additional_charge_id uuid REFERENCES bonbon.additional_charge(additional_charge_id);
ALTER TABLE bonbon.payment ADD CONSTRAINT ck_payment_return_charge CHECK (
    (payment_purpose='Rental' AND additional_charge_id IS NULL) OR
    (payment_purpose='ExcessMileage' AND additional_charge_id IS NOT NULL AND provider='SEPAY_SANDBOX'));
DROP INDEX bonbon.uq_sepay_sandbox_active;
DROP INDEX bonbon.uq_sepay_sandbox_accepted_success;
DROP INDEX bonbon.uq_sandbox_active_booking;
DROP INDEX bonbon.uq_sandbox_accepted_booking;
CREATE UNIQUE INDEX uq_sepay_sandbox_active ON bonbon.payment(booking_id,payment_purpose)
    WHERE provider='SEPAY_SANDBOX' AND status IN ('Pending','Processing');
CREATE UNIQUE INDEX uq_sepay_sandbox_accepted_success ON bonbon.payment(booking_id,payment_purpose)
    WHERE provider='SEPAY_SANDBOX' AND status='Succeeded' AND NOT needs_reconciliation;
CREATE UNIQUE INDEX uq_sandbox_active_booking ON bonbon.payment(booking_id,payment_purpose)
    WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX','SEPAY_SANDBOX') AND status IN('Pending','Processing');
CREATE UNIQUE INDEX uq_sandbox_accepted_booking ON bonbon.payment(booking_id,payment_purpose)
    WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX','SEPAY_SANDBOX') AND status='Succeeded' AND NOT needs_reconciliation;
COMMIT;
