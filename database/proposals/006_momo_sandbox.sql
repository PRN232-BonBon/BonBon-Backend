-- PROPOSAL. Execute only after target verification/backup. Never bootstrap at startup.
-- Keeps every VNPay receipt/index/constraint. Existing audit rows remain VNPAY_SANDBOX.
BEGIN;
SET LOCAL lock_timeout='2s';
SET LOCAL statement_timeout='30s';
DO $$ BEGIN
 IF to_regclass('bonbon.payment_gateway_schema') IS NULL OR to_regclass('bonbon.momo_gateway_schema') IS NOT NULL THEN
  RAISE EXCEPTION 'Requires reviewed Payment004 baseline and no previous MoMo proposal';
 END IF;
 IF EXISTS(SELECT 1 FROM bonbon.payment WHERE provider='MOMO_SANDBOX') THEN RAISE EXCEPTION 'Inspect existing MoMo rows'; END IF;
END $$;
ALTER TABLE bonbon.payment ALTER COLUMN provider_transaction_no TYPE varchar(20);
CREATE TABLE bonbon.momo_payment_request (
 payment_id uuid PRIMARY KEY REFERENCES bonbon.payment(payment_id) ON DELETE RESTRICT,
 request_id varchar(50) NOT NULL UNIQUE,
 partner_code varchar(50) NOT NULL CHECK(btrim(partner_code)<>''),
 creation_state varchar(20) NOT NULL DEFAULT 'Reserved' CHECK(creation_state IN('Reserved','Sent','Ready','Uncertain')),
 last_result_code integer,
 updated_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE bonbon.payment ADD CONSTRAINT ck_momo_sandbox_attempt CHECK(provider<>'MOMO_SANDBOX' OR (
 provider_reference IS NOT NULL AND provider_reference ~ '^[a-f0-9]{32}$' AND idempotency_key IS NOT NULL
 AND idempotency_key ~ '^[A-Za-z0-9_-]{16,100}$' AND currency='VND' AND amount BETWEEN 1000 AND 50000000
 AND isfinite(created_at) AND (paid_at IS NULL OR isfinite(paid_at))
 AND expires_at IS NOT NULL AND isfinite(expires_at) AND expires_at>created_at AND expires_at<=created_at+interval '30 minutes'
 AND updated_at IS NOT NULL AND isfinite(updated_at) AND updated_at>=created_at
 AND checkout_url IS NOT NULL AND (checkout_url='momo:create:reserved' OR checkout_url LIKE 'https://test-payment.momo.vn/v2/gateway/pay?%')
 AND (provider_transaction_no IS NULL OR provider_transaction_no ~ '^[1-9][0-9]{0,18}$')
 AND (status<>'Succeeded' OR needs_reconciliation OR provider_transaction_no IS NOT NULL)
 AND ((needs_reconciliation AND reconciliation_reason IS NOT NULL AND btrim(reconciliation_reason)<>'') OR (NOT needs_reconciliation AND reconciliation_reason IS NULL))));
CREATE UNIQUE INDEX uq_momo_sandbox_idempotency ON bonbon.payment(booking_id,idempotency_key) WHERE provider='MOMO_SANDBOX';
CREATE UNIQUE INDEX uq_momo_sandbox_active ON bonbon.payment(booking_id) WHERE provider='MOMO_SANDBOX' AND status IN('Pending','Processing');
CREATE UNIQUE INDEX uq_momo_sandbox_accepted_success ON bonbon.payment(booking_id) WHERE provider='MOMO_SANDBOX' AND status='Succeeded' AND NOT needs_reconciliation;
CREATE UNIQUE INDEX uq_momo_sandbox_transaction ON bonbon.payment(provider_transaction_no) WHERE provider='MOMO_SANDBOX' AND provider_transaction_no IS NOT NULL;
-- Atomic cross-provider protection. Refuses conflicting old data instead of deleting it.
CREATE UNIQUE INDEX uq_sandbox_active_booking ON bonbon.payment(booking_id) WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX') AND status IN('Pending','Processing');
CREATE UNIQUE INDEX uq_sandbox_accepted_booking ON bonbon.payment(booking_id) WHERE provider IN('VNPAY_SANDBOX','MOMO_SANDBOX') AND status='Succeeded' AND NOT needs_reconciliation;
ALTER TABLE bonbon.payment_notification
 ADD COLUMN gateway_provider varchar(50) NOT NULL DEFAULT 'VNPAY_SANDBOX' CHECK(gateway_provider IN('VNPAY_SANDBOX','MOMO_SANDBOX')),
 ADD COLUMN gateway_result_code integer,
 ALTER COLUMN provider_transaction_no TYPE varchar(20);
ALTER TABLE bonbon.payment_notification DROP CONSTRAINT payment_notification_provider_transaction_no_check,
 DROP CONSTRAINT payment_notification_disposition_check;
ALTER TABLE bonbon.payment_notification ADD CONSTRAINT ck_notification_transaction_provider CHECK (
 provider_transaction_no IS NULL OR (provider_transaction_no ~ '^[0-9]+$' AND provider_transaction_no !~ '^0+$'
 AND char_length(provider_transaction_no)<=CASE WHEN gateway_provider='MOMO_SANDBOX' THEN 19 ELSE 15 END));
ALTER TABLE bonbon.payment_notification ADD CONSTRAINT ck_notification_disposition_provider CHECK (
 disposition IN('Success','Failure','ReconcileSuccess','ReconcilePending','Duplicate') OR (gateway_provider='MOMO_SANDBOX' AND disposition='Processing'));
ALTER TABLE bonbon.payment_notification ADD CONSTRAINT ck_notification_raw_momo CHECK(gateway_provider<>'MOMO_SANDBOX' OR gateway_result_code IS NOT NULL);
CREATE TABLE bonbon.momo_gateway_schema(version integer PRIMARY KEY CHECK(version=1));
INSERT INTO bonbon.momo_gateway_schema VALUES(1);
-- Grants are a separate DBA step for the verified isolated target; no default grants.
-- After committed receipts exist, roll forward; never drop audit/receipt history.
COMMIT;
