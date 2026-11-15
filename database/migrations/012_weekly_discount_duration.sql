-- Apply after 011_pending_booking_holds.sql; existing booking snapshots are unchanged.
BEGIN;

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
    IF p_start_at <= clock_timestamp() THEN
        RAISE EXCEPTION 'Pickup time must be in the future' USING ERRCODE = '23514';
    END IF;
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
        IF duration_seconds >= 604800 AND selected_vehicle.weekly_discount_enabled THEN
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
        included_mileage_km_snapshot, overage_fee_per_km_snapshot, rental_terms_snapshot, status, hold_expires_at
    ) VALUES (
        p_customer_account_id, p_vehicle_id, p_rental_type, selected_billable_units, p_start_at, p_end_at,
        selected_price, selected_discount_percent, selected_discount_amount,
        selected_included_mileage, selected_overage_fee, selected_vehicle.rental_terms, 'AwaitingPayment',
        LEAST(clock_timestamp() + interval '15 minutes', p_start_at)
    ) RETURNING * INTO result;

    RETURN result;
END;
$$;

COMMIT;
