BEGIN;

ALTER TABLE bonbon.vehicle
    ADD COLUMN IF NOT EXISTS weekly_discount_enabled boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS weekly_discount_percent numeric(5,2),
    ADD COLUMN IF NOT EXISTS mileage_limit_enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN IF NOT EXISTS daily_mileage_limit_km integer,
    ADD COLUMN IF NOT EXISTS overage_fee_per_km numeric(18,2),
    ADD COLUMN IF NOT EXISTS rental_terms text,
    ADD COLUMN IF NOT EXISTS rejection_reason text,
    ADD COLUMN IF NOT EXISTS reviewed_at timestamptz;

UPDATE bonbon.vehicle
SET daily_mileage_limit_km = COALESCE(daily_mileage_limit_km, 400),
    overage_fee_per_km = COALESCE(overage_fee_per_km, 3000)
WHERE mileage_limit_enabled;

ALTER TABLE bonbon.vehicle
    DROP CONSTRAINT IF EXISTS ck_vehicle_weekly_discount,
    DROP CONSTRAINT IF EXISTS ck_vehicle_mileage_limit,
    DROP CONSTRAINT IF EXISTS ck_vehicle_rental_terms,
    DROP CONSTRAINT IF EXISTS vehicle_status_check;

ALTER TABLE bonbon.vehicle
    ADD CONSTRAINT ck_vehicle_weekly_discount CHECK (
        (NOT weekly_discount_enabled AND weekly_discount_percent IS NULL)
        OR (weekly_discount_enabled AND weekly_discount_percent BETWEEN 5 AND 30)
    ),
    ADD CONSTRAINT ck_vehicle_mileage_limit CHECK (
        (NOT mileage_limit_enabled AND daily_mileage_limit_km IS NULL AND overage_fee_per_km IS NULL)
        OR (mileage_limit_enabled
            AND daily_mileage_limit_km BETWEEN 100 AND 800
            AND overage_fee_per_km BETWEEN 1000 AND 10000
            AND overage_fee_per_km = trunc(overage_fee_per_km))
    ),
    ADD CONSTRAINT ck_vehicle_rental_terms CHECK (
        rental_terms IS NULL OR char_length(btrim(rental_terms)) BETWEEN 10 AND 1000
    ),
    ADD CONSTRAINT vehicle_status_check CHECK (
        status IN ('Draft', 'PendingApproval', 'Available', 'Rejected', 'Suspended', 'Archived')
    );

ALTER TABLE bonbon.vehicle_image
    ALTER COLUMN image_url DROP NOT NULL,
    ADD COLUMN IF NOT EXISTS image_data bytea,
    ADD COLUMN IF NOT EXISTS content_type varchar(50),
    ADD COLUMN IF NOT EXISTS byte_length integer,
    ADD COLUMN IF NOT EXISTS width integer,
    ADD COLUMN IF NOT EXISTS height integer;

ALTER TABLE bonbon.vehicle_image
    DROP CONSTRAINT IF EXISTS vehicle_image_image_url_check,
    DROP CONSTRAINT IF EXISTS ck_vehicle_image_source,
    DROP CONSTRAINT IF EXISTS ck_vehicle_image_metadata;

ALTER TABLE bonbon.vehicle_image
    ADD CONSTRAINT ck_vehicle_image_source CHECK (
        (image_url IS NOT NULL AND btrim(image_url) <> '' AND image_data IS NULL)
        OR (image_url IS NULL AND image_data IS NOT NULL)
    ),
    ADD CONSTRAINT ck_vehicle_image_metadata CHECK (
        image_data IS NULL OR (
            content_type = 'image/webp'
            AND byte_length = octet_length(image_data)
            AND byte_length BETWEEN 1 AND 2097152
            AND width BETWEEN 1 AND 1600
            AND height BETWEEN 1 AND 1600
        )
    );

CREATE TABLE IF NOT EXISTS bonbon.vehicle_document (
    vehicle_document_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    vehicle_id uuid NOT NULL REFERENCES bonbon.vehicle(vehicle_id) ON DELETE CASCADE,
    document_type varchar(40) NOT NULL CHECK (document_type IN ('RegistrationCertificate')),
    document_data bytea NOT NULL,
    content_type varchar(50) NOT NULL CHECK (content_type = 'image/webp'),
    byte_length integer NOT NULL,
    width integer NOT NULL,
    height integer NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_vehicle_document_metadata CHECK (
        byte_length = octet_length(document_data)
        AND byte_length BETWEEN 1 AND 2097152
        AND width BETWEEN 1 AND 1600
        AND height BETWEEN 1 AND 1600
    ),
    CONSTRAINT uq_vehicle_document_type UNIQUE (vehicle_id, document_type)
);

ALTER TABLE bonbon.booking
    ADD COLUMN IF NOT EXISTS weekly_discount_percent_snapshot numeric(5,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS discount_amount_snapshot numeric(18,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS included_mileage_km_snapshot integer,
    ADD COLUMN IF NOT EXISTS overage_fee_per_km_snapshot numeric(18,2),
    ADD COLUMN IF NOT EXISTS rental_terms_snapshot text;

ALTER TABLE bonbon.booking
    DROP CONSTRAINT IF EXISTS ck_booking_discount_snapshot,
    DROP CONSTRAINT IF EXISTS ck_booking_mileage_snapshot;

ALTER TABLE bonbon.booking
    ADD CONSTRAINT ck_booking_discount_snapshot CHECK (
        weekly_discount_percent_snapshot BETWEEN 0 AND 30
        AND discount_amount_snapshot >= 0
        AND discount_amount_snapshot < billable_units * unit_price_snapshot
        AND discount_amount_snapshot = trunc(discount_amount_snapshot)
    ),
    ADD CONSTRAINT ck_booking_mileage_snapshot CHECK (
        (included_mileage_km_snapshot IS NULL AND overage_fee_per_km_snapshot IS NULL)
        OR (included_mileage_km_snapshot > 0
            AND overage_fee_per_km_snapshot BETWEEN 1000 AND 10000
            AND overage_fee_per_km_snapshot = trunc(overage_fee_per_km_snapshot))
    );

ALTER TABLE bonbon.booking DROP COLUMN quoted_rental_amount;
ALTER TABLE bonbon.booking ADD COLUMN quoted_rental_amount numeric(18,2)
    GENERATED ALWAYS AS (
        billable_units * unit_price_snapshot - discount_amount_snapshot
    ) STORED;

CREATE OR REPLACE FUNCTION bonbon.create_booking(
    p_customer_account_id uuid,
    p_vehicle_id uuid,
    p_rental_type varchar,
    p_start_at timestamptz,
    p_end_at timestamptz
) RETURNS bonbon.booking LANGUAGE plpgsql SET search_path = bonbon, public AS $$
DECLARE
    selected_vehicle bonbon.vehicle%ROWTYPE;
    result bonbon.booking%ROWTYPE;
    selected_price numeric(18,2);
    selected_billable_units integer;
    selected_discount_percent numeric(5,2) := 0;
    selected_discount_amount numeric(18,2) := 0;
    selected_included_mileage integer;
    selected_overage_fee numeric(18,2);
    duration_seconds numeric;
    local_start timestamp;
    local_end timestamp;
BEGIN
    IF NOT bonbon.has_active_role(p_customer_account_id, 2::smallint) THEN
        RAISE EXCEPTION 'Active Customer role and platform Account are required' USING ERRCODE = '23514';
    END IF;

    SELECT * INTO selected_vehicle FROM bonbon.vehicle WHERE vehicle_id = p_vehicle_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Vehicle does not exist' USING ERRCODE = '23503'; END IF;
    IF selected_vehicle.status <> 'Available' THEN
        RAISE EXCEPTION 'Vehicle is not available for booking' USING ERRCODE = '23514';
    END IF;
    IF NOT bonbon.has_active_role(selected_vehicle.owner_account_id, 3::smallint) THEN
        RAISE EXCEPTION 'Active VehicleOwner role and platform Account are required' USING ERRCODE = '23514';
    END IF;

    duration_seconds := extract(epoch FROM (p_end_at - p_start_at));
    local_start := p_start_at AT TIME ZONE 'Asia/Ho_Chi_Minh';
    local_end := p_end_at AT TIME ZONE 'Asia/Ho_Chi_Minh';

    IF p_rental_type = 'Hourly'
       AND duration_seconds BETWEEN 14400 AND 28800
       AND mod(duration_seconds::bigint, 3600) = 0
       AND extract(second FROM local_start) = 0
       AND extract(minute FROM local_start) IN (0, 30) THEN
        selected_billable_units := (duration_seconds / 3600)::integer;
        selected_price := selected_vehicle.hour_price;
    ELSIF p_rental_type = 'Daily'
       AND local_end::date > local_start::date
       AND duration_seconds <= 2592000 THEN
        selected_billable_units := ceil(duration_seconds / 86400)::integer;
        selected_price := selected_vehicle.day_price;
        IF selected_billable_units >= 7 AND selected_vehicle.weekly_discount_enabled THEN
            selected_discount_percent := selected_vehicle.weekly_discount_percent;
            selected_discount_amount := round(
                selected_billable_units * selected_price * selected_discount_percent / 100
            );
        END IF;
    ELSE
        RAISE EXCEPTION 'Invalid rental interval' USING ERRCODE = '23514';
    END IF;

    IF selected_vehicle.mileage_limit_enabled THEN
        selected_included_mileage := selected_vehicle.daily_mileage_limit_km
            * CASE WHEN p_rental_type = 'Daily' THEN selected_billable_units ELSE 1 END;
        selected_overage_fee := selected_vehicle.overage_fee_per_km;
    END IF;

    IF EXISTS (
        SELECT 1 FROM bonbon.booking b WHERE b.vehicle_id = p_vehicle_id
          AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup', 'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
          AND tstzrange(b.start_at, b.end_at, '[)') && tstzrange(p_start_at, p_end_at, '[)')
    ) THEN
        RAISE EXCEPTION 'Vehicle already reserved for this period' USING ERRCODE = '23P01';
    END IF;

    INSERT INTO bonbon.booking (
        customer_account_id, vehicle_id, rental_type, billable_units, start_at, end_at,
        unit_price_snapshot, weekly_discount_percent_snapshot, discount_amount_snapshot,
        included_mileage_km_snapshot, overage_fee_per_km_snapshot, rental_terms_snapshot
    ) VALUES (
        p_customer_account_id, p_vehicle_id, p_rental_type, selected_billable_units, p_start_at, p_end_at,
        selected_price, selected_discount_percent, selected_discount_amount,
        selected_included_mileage, selected_overage_fee, selected_vehicle.rental_terms
    ) RETURNING * INTO result;

    RETURN result;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_additional_charge_excess_mileage
    ON bonbon.additional_charge (booking_id, reason_code)
    WHERE reason_code = 'EXCESS_MILEAGE';

COMMENT ON COLUMN bonbon.vehicle_image.image_data IS 'WebP bytes for owner-uploaded public listing images.';
COMMENT ON TABLE bonbon.vehicle_document IS 'Private owner evidence. Never expose through the public vehicle API.';
COMMENT ON COLUMN bonbon.vehicle_inspection.odometer_km IS 'Pickup and return readings used for mileage settlement when both are present.';

COMMIT;
