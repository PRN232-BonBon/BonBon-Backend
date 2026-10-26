BEGIN;

ALTER TABLE bonbon.vehicle
    ADD COLUMN IF NOT EXISTS submitted_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS reviewed_by uuid REFERENCES bonbon.account(account_id);

CREATE INDEX IF NOT EXISTS ix_vehicle_review_queue ON bonbon.vehicle(status, submitted_at DESC);

COMMIT;
