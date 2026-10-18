BEGIN;

ALTER TABLE bonbon.booking
    DROP CONSTRAINT ck_booking_billable_units,
    DROP CONSTRAINT ck_booking_duration;

ALTER TABLE bonbon.booking
    ADD CONSTRAINT ck_booking_billable_units CHECK (
        (rental_type = 'Hourly' AND billable_units BETWEEN 4 AND 8)
        OR (rental_type = 'Daily' AND billable_units BETWEEN 1 AND 30)
    ),
    ADD CONSTRAINT ck_booking_duration CHECK (
        (
            rental_type = 'Hourly'
            AND extract(epoch FROM (end_at - start_at)) = billable_units::numeric * 3600
            AND extract(second FROM (start_at AT TIME ZONE 'Asia/Ho_Chi_Minh')) = 0
            AND extract(minute FROM (start_at AT TIME ZONE 'Asia/Ho_Chi_Minh')) IN (0, 30)
        )
        OR
        (
            rental_type = 'Daily'
            AND (end_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
                > (start_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date
            AND extract(epoch FROM (end_at - start_at))
                > (billable_units - 1)::numeric * 86400
            AND extract(epoch FROM (end_at - start_at))
                <= billable_units::numeric * 86400
            AND extract(epoch FROM (end_at - start_at)) <= 2592000
        )
    );

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
    duration_seconds numeric;
    local_start timestamp;
    local_end timestamp;
BEGIN
    IF NOT bonbon.has_active_role(p_customer_account_id, 2::smallint) THEN
        RAISE EXCEPTION 'Active Customer role and platform Account are required'
            USING ERRCODE = '23514';
    END IF;

    SELECT * INTO selected_vehicle FROM bonbon.vehicle
    WHERE vehicle_id = p_vehicle_id FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Vehicle does not exist' USING ERRCODE = '23503';
    END IF;
    IF selected_vehicle.status <> 'Available' THEN
        RAISE EXCEPTION 'Vehicle is not available for booking' USING ERRCODE = '23514';
    END IF;
    IF NOT bonbon.has_active_role(selected_vehicle.owner_account_id, 3::smallint) THEN
        RAISE EXCEPTION 'Active VehicleOwner role and platform Account are required'
            USING ERRCODE = '23514';
    END IF;
    IF p_start_at IS NULL OR p_end_at IS NULL
       OR NOT isfinite(p_start_at) OR NOT isfinite(p_end_at)
       OR p_start_at >= p_end_at OR p_rental_type IS NULL THEN
        RAISE EXCEPTION 'A valid rental type, start time and end time are required'
            USING ERRCODE = '23514';
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
    ELSE
        RAISE EXCEPTION 'Hourly rental requires 4-8 whole hours on a 30-minute slot; daily rental must end on a later local date and cannot exceed 30 days'
            USING ERRCODE = '23514';
    END IF;

    IF EXISTS (
        SELECT 1 FROM bonbon.booking b WHERE b.vehicle_id = p_vehicle_id
          AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
              'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
          AND tstzrange(b.start_at, b.end_at, '[)') &&
              tstzrange(p_start_at, p_end_at, '[)')) THEN
        RAISE EXCEPTION 'Vehicle already reserved for this period' USING ERRCODE = '23P01';
    END IF;

    INSERT INTO bonbon.booking (
        customer_account_id, vehicle_id, rental_type, billable_units,
        start_at, end_at, unit_price_snapshot
    ) VALUES (
        p_customer_account_id, p_vehicle_id, p_rental_type, selected_billable_units,
        p_start_at, p_end_at, selected_price
    ) RETURNING * INTO result;

    RETURN result;
END;
$$;

COMMENT ON COLUMN bonbon.booking.billable_units IS
    'Hourly: exact hours from 4 to 8. Daily: ceil of elapsed time divided by 24 hours, up to 30 days.';

COMMIT;
