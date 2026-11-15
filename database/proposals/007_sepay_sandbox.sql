-- PROPOSAL: reviewed 004 + 005 + 006 baseline; never run automatically at startup.
-- No UPDATE/DELETE of historical Payment/Booking. Back up and verify the target first.
BEGIN;
SET LOCAL lock_timeout='2s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF to_regclass('bonbon.momo_gateway_schema') IS NULL OR to_regclass('bonbon.sepay_gateway_schema') IS NOT NULL
 OR NOT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1)
 OR NOT EXISTS(SELECT 1 FROM bonbon.momo_gateway_schema WHERE version=1) THEN
  RAISE EXCEPTION 'Requires reviewed 004/006 baseline and no existing SePay marker';
 END IF;
 IF EXISTS(SELECT 1 FROM bonbon.payment WHERE provider='SEPAY_SANDBOX') THEN RAISE EXCEPTION 'Inspect existing SePay data before migration'; END IF;
 IF (SELECT count(*) FROM pg_constraint WHERE conrelid='bonbon.payment_notification'::regclass AND conname IN
 ('payment_notification_gateway_provider_check','ck_notification_transaction_provider'))<>2 THEN RAISE EXCEPTION 'Audit constraint drift'; END IF;
END $$;
ALTER TABLE bonbon.payment ALTER COLUMN provider_transaction_no TYPE varchar(100);
ALTER TABLE bonbon.payment ADD CONSTRAINT ck_sepay_sandbox_attempt CHECK(provider<>'SEPAY_SANDBOX' OR (
 provider_reference IS NOT NULL AND provider_reference ~ '^[a-f0-9]{32}$'
 AND idempotency_key IS NOT NULL AND idempotency_key ~ '^[A-Za-z0-9_-]{16,100}$'
 AND currency='VND' AND amount BETWEEN 1 AND 9999999999 AND amount=trunc(amount)
 AND isfinite(created_at) AND expires_at IS NOT NULL AND isfinite(expires_at)
 AND expires_at>created_at AND expires_at<=created_at+interval '30 minutes'
 AND updated_at IS NOT NULL AND isfinite(updated_at) AND updated_at>=created_at
 AND (paid_at IS NULL OR isfinite(paid_at))
 AND checkout_url IS NOT NULL AND checkout_url ~ '^sepay:form:[a-f0-9]{64}$'
 AND (provider_transaction_no IS NULL OR provider_transaction_no ~ '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$')
 AND (status<>'Succeeded' OR needs_reconciliation OR (provider_transaction_no IS NOT NULL AND paid_at IS NOT NULL))
 AND ((needs_reconciliation AND reconciliation_reason IS NOT NULL AND btrim(reconciliation_reason)<>'') OR (NOT needs_reconciliation AND reconciliation_reason IS NULL))));
CREATE UNIQUE INDEX uq_sepay_sandbox_idempotency ON bonbon.payment(booking_id,idempotency_key) WHERE provider='SEPAY_SANDBOX';
CREATE UNIQUE INDEX uq_sepay_sandbox_active ON bonbon.payment(booking_id) WHERE provider='SEPAY_SANDBOX' AND status IN('Pending','Processing');
CREATE UNIQUE INDEX uq_sepay_sandbox_accepted_success ON bonbon.payment(booking_id) WHERE provider='SEPAY_SANDBOX' AND status='Succeeded' AND NOT needs_reconciliation;
CREATE UNIQUE INDEX uq_sepay_sandbox_transaction ON bonbon.payment(provider_transaction_no) WHERE provider='SEPAY_SANDBOX' AND provider_transaction_no IS NOT NULL;
-- Index replacement is transactional; conflicting old rows abort the entire migration.
DROP INDEX bonbon.uq_sandbox_active_booking;
DROP INDEX bonbon.uq_sandbox_accepted_booking;
CREATE UNIQUE INDEX uq_sandbox_active_booking ON bonbon.payment(booking_id) WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX','SEPAY_SANDBOX') AND status IN('Pending','Processing');
CREATE UNIQUE INDEX uq_sandbox_accepted_booking ON bonbon.payment(booking_id) WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX','SEPAY_SANDBOX') AND status='Succeeded' AND NOT needs_reconciliation;
ALTER TABLE bonbon.payment_notification ALTER COLUMN provider_transaction_no TYPE varchar(100);
ALTER TABLE bonbon.payment_notification DROP CONSTRAINT payment_notification_gateway_provider_check,
 DROP CONSTRAINT ck_notification_transaction_provider;
ALTER TABLE bonbon.payment_notification ADD CONSTRAINT payment_notification_gateway_provider_check CHECK(gateway_provider IN('VNPAY_SANDBOX','MOMO_SANDBOX','SEPAY_SANDBOX'));
ALTER TABLE bonbon.payment_notification ADD CONSTRAINT ck_notification_transaction_provider CHECK(provider_transaction_no IS NULL OR (
 (gateway_provider='SEPAY_SANDBOX' AND provider_transaction_no ~ '^[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}$') OR
 (gateway_provider IN('VNPAY_SANDBOX','MOMO_SANDBOX') AND provider_transaction_no ~ '^[0-9]+$' AND provider_transaction_no !~ '^0+$'
 AND char_length(provider_transaction_no)<=CASE WHEN gateway_provider='MOMO_SANDBOX' THEN 19 ELSE 15 END)));
CREATE TABLE bonbon.sepay_payment_evidence(
 payment_id uuid NOT NULL REFERENCES bonbon.payment(payment_id) ON DELETE RESTRICT,
 fingerprint char(64) NOT NULL,
 evidence varchar(1000) NOT NULL CHECK(btrim(evidence)<>''),
 PRIMARY KEY(payment_id,fingerprint),
 FOREIGN KEY(payment_id,fingerprint) REFERENCES bonbon.payment_notification(payment_id,fingerprint) ON DELETE RESTRICT
);
CREATE TABLE bonbon.sepay_rejected_notification(
 fingerprint char(64) PRIMARY KEY CHECK(fingerprint ~ '^[a-f0-9]{64}$'),
 reason varchar(100) NOT NULL CHECK(reason ~ '^[A-Z_]+$'),
 received_at timestamptz NOT NULL CHECK(isfinite(received_at))
);
CREATE TABLE bonbon.sepay_gateway_schema(version integer PRIMARY KEY CHECK(version=1));
INSERT INTO bonbon.sepay_gateway_schema VALUES(1);
-- Runtime grants are a separate reviewed DBA step for the isolated target.
-- Once receipts exist, roll forward; never remove financial/audit history.
COMMIT;
