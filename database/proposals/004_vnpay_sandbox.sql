-- PROPOSAL for shared bonbon: NOT executed there. Tested on bonbon_payment_test in 2E.2.
-- Shared deployment still requires separate approval, backup and operational review.
-- Do not mount in docker-entrypoint-initdb.d or run at application startup.
-- One-shot upgrade of the flexible-booking schema, NOT a 001 -> 002 -> 003 runner.
-- Future execution requires psql -X -v ON_ERROR_STOP=1, a tested backup, a short
-- maintenance window and an explicitly approved target. Never execute for review.
BEGIN;
SET LOCAL lock_timeout = '2s';
SET LOCAL statement_timeout = '30s';
SET LOCAL idle_in_transaction_session_timeout = '30s';

-- Fail rather than silently accepting an earlier/partial/incompatible deployment.
DO $$ BEGIN
    IF to_regclass('bonbon.payment_gateway_schema') IS NOT NULL
       OR to_regclass('bonbon.payment_notification') IS NOT NULL
       OR EXISTS (SELECT 1 FROM pg_attribute a
          WHERE a.attrelid = to_regclass('bonbon.payment') AND NOT a.attisdropped
            AND a.attname IN ('idempotency_key','checkout_url','expires_at',
                'provider_transaction_no','response_code','transaction_status',
                'updated_at','needs_reconciliation','reconciliation_reason')) THEN
        RAISE EXCEPTION 'Payment proposal objects already exist; inspect deployment history, do not rerun';
    END IF;
    IF EXISTS (
        SELECT 1 FROM (VALUES
            ('payment','payment_id','uuid'), ('payment','booking_id','uuid'),
            ('payment','provider_reference','character varying(200)'),
            ('payment','provider','character varying(50)'), ('payment','amount','numeric(18,2)'),
            ('payment','currency','character varying(3)'), ('payment','status','character varying(20)'),
            ('payment','created_at','timestamp with time zone'), ('payment','paid_at','timestamp with time zone'),
            ('booking','booking_id','uuid'), ('booking','customer_account_id','uuid'),
            ('booking','vehicle_id','uuid'), ('booking','billable_units','integer'),
            ('booking','quoted_rental_amount','numeric(18,2)'),
            ('booking','start_at','timestamp with time zone'), ('booking','end_at','timestamp with time zone')
        ) AS expected(table_name,column_name,type_name)
        LEFT JOIN pg_namespace n ON n.nspname='bonbon'
        LEFT JOIN pg_class c ON c.relnamespace=n.oid AND c.relname=expected.table_name AND c.relkind='r'
        LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attname=expected.column_name AND NOT a.attisdropped
        WHERE a.attnum IS NULL OR format_type(a.atttypid,a.atttypmod) <> expected.type_name
    ) THEN
        RAISE EXCEPTION 'Incompatible baseline: review actual flexible-booking/payment column types first';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint co JOIN pg_attribute a ON a.attrelid=co.conrelid
        WHERE co.conrelid='bonbon.payment'::regclass AND co.contype='u' AND co.convalidated
          AND a.attname='provider_reference' AND co.conkey=ARRAY[a.attnum]
    ) THEN
        RAISE EXCEPTION 'Existing unique provider_reference constraint is required';
    END IF;
END $$;

-- The provider is intentionally distinct from a future production provider.
-- Refuse migration if someone already used this reserved provider on an older schema.
-- This is the same lock class required by ALTER; acquire before the row preflight
-- so another writer cannot introduce a reserved-provider row between check and DDL.
LOCK TABLE bonbon.payment IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM bonbon.payment WHERE provider = 'VNPAY_SANDBOX') THEN
        RAISE EXCEPTION 'Review existing VNPAY_SANDBOX payments before applying this proposal';
    END IF;
END $$;

ALTER TABLE bonbon.payment
    ADD COLUMN idempotency_key varchar(100),
    ADD COLUMN checkout_url text,
    ADD COLUMN expires_at timestamptz,
    ADD COLUMN provider_transaction_no varchar(15),
    ADD COLUMN response_code varchar(2),
    ADD COLUMN transaction_status varchar(2),
    ADD COLUMN updated_at timestamptz,
    ADD COLUMN needs_reconciliation boolean NOT NULL DEFAULT false,
    ADD COLUMN reconciliation_reason varchar(100),
    ADD CONSTRAINT ck_vnpay_sandbox_attempt CHECK (provider <> 'VNPAY_SANDBOX' OR (
        provider_reference IS NOT NULL AND char_length(provider_reference) BETWEEN 1 AND 100
        AND provider_reference !~ '[^A-Za-z0-9]'
        AND idempotency_key IS NOT NULL AND char_length(idempotency_key) BETWEEN 16 AND 100
        AND idempotency_key !~ '[^A-Za-z0-9_-]'
        AND amount BETWEEN 1 AND 9999999999 AND currency = 'VND'
        AND isfinite(created_at) AND expires_at IS NOT NULL AND isfinite(expires_at)
        AND expires_at > created_at AND expires_at <= created_at + interval '30 minutes'
        AND checkout_url IS NOT NULL
        AND checkout_url LIKE 'https://sandbox.vnpayment.vn/paymentv2/vpcpay.html?%'
        AND updated_at IS NOT NULL AND isfinite(updated_at) AND updated_at >= created_at
        AND (paid_at IS NULL OR isfinite(paid_at))
        AND (response_code IS NULL OR (char_length(response_code)=2 AND response_code !~ '[^0-9]'))
        AND (transaction_status IS NULL OR (char_length(transaction_status)=2 AND transaction_status !~ '[^0-9]'))
        AND (provider_transaction_no IS NULL OR (char_length(provider_transaction_no) BETWEEN 1 AND 15
            AND provider_transaction_no !~ '[^0-9]' AND provider_transaction_no !~ '^0+$'))
        AND (status <> 'Succeeded' OR needs_reconciliation OR provider_transaction_no IS NOT NULL)
        AND ((needs_reconciliation AND reconciliation_reason IS NOT NULL AND btrim(reconciliation_reason) <> '')
            OR (NOT needs_reconciliation AND reconciliation_reason IS NULL))));

CREATE UNIQUE INDEX uq_vnpay_sandbox_idempotency ON bonbon.payment (booking_id, idempotency_key)
    WHERE provider = 'VNPAY_SANDBOX';
CREATE UNIQUE INDEX uq_vnpay_sandbox_active ON bonbon.payment (booking_id)
    WHERE provider = 'VNPAY_SANDBOX' AND status IN ('Pending', 'Processing');
-- Extra receipts are retained with needs_reconciliation=true, never discarded.
CREATE UNIQUE INDEX uq_vnpay_sandbox_accepted_success ON bonbon.payment (booking_id)
    WHERE provider = 'VNPAY_SANDBOX' AND status = 'Succeeded' AND NOT needs_reconciliation;
CREATE UNIQUE INDEX uq_vnpay_sandbox_transaction ON bonbon.payment (provider_transaction_no)
    WHERE provider = 'VNPAY_SANDBOX' AND provider_transaction_no IS NOT NULL;

CREATE TABLE bonbon.payment_notification (
    payment_notification_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    payment_id uuid NOT NULL REFERENCES bonbon.payment(payment_id) ON DELETE RESTRICT,
    fingerprint varchar(64) NOT NULL CHECK (char_length(fingerprint)=64 AND fingerprint !~ '[^0-9A-Fa-f]'),
    provider_transaction_no varchar(15) CHECK (provider_transaction_no IS NULL OR
        (char_length(provider_transaction_no) BETWEEN 1 AND 15 AND provider_transaction_no !~ '[^0-9]' AND provider_transaction_no !~ '^0+$')),
    amount_minor bigint NOT NULL CHECK (amount_minor BETWEEN 100 AND 999999999900 AND amount_minor % 100 = 0),
    response_code varchar(2) NOT NULL CHECK (char_length(response_code)=2 AND response_code !~ '[^0-9]'),
    transaction_status varchar(2) NOT NULL CHECK (char_length(transaction_status)=2 AND transaction_status !~ '[^0-9]'),
    gateway_paid_at timestamptz CHECK (gateway_paid_at IS NULL OR isfinite(gateway_paid_at)),
    received_at timestamptz NOT NULL CHECK (isfinite(received_at)),
    disposition varchar(30) NOT NULL CHECK (disposition IN ('Success','Failure','ReconcileSuccess','ReconcilePending','Duplicate')),
    reconciliation_reason varchar(100),
    CONSTRAINT ck_payment_notification_receipt CHECK (disposition NOT IN ('Success','ReconcileSuccess') OR
        (gateway_paid_at IS NOT NULL AND provider_transaction_no IS NOT NULL AND response_code='00' AND transaction_status='00')),
    CONSTRAINT ck_payment_notification_reconciliation CHECK (
        (disposition IN ('ReconcileSuccess','ReconcilePending') AND reconciliation_reason IS NOT NULL AND btrim(reconciliation_reason)<>'')
        OR (disposition NOT IN ('ReconcileSuccess','ReconcilePending') AND reconciliation_reason IS NULL)),
    UNIQUE (payment_id, fingerprint)
);
CREATE INDEX ix_payment_notification_received ON bonbon.payment_notification (payment_id, received_at);

-- Marker is committed atomically with this proposal. Repositories also check indexes/columns.
CREATE TABLE bonbon.payment_gateway_schema (version integer PRIMARY KEY CHECK (version = 1));
INSERT INTO bonbon.payment_gateway_schema (version) VALUES (1);
-- This proposal does not repair booking triggers, grant application permissions,
-- install reconciliation jobs or guarantee uniqueness across other providers.
-- Review those deployment gates separately before enabling Payment.
-- Any error before COMMIT aborts this transaction: issue ROLLBACK/disconnect.
-- After COMMIT with receipts present, preserve audit data and roll forward;
-- never blindly drop these columns/tables as a production "down" migration.
COMMIT;
