-- Explicit operator-run seed only. Never run automatically at startup or as a schema migration.
-- User-approved demo listing: 5,000 VND/day (VNPay minimum); no weekly discount.
BEGIN;
DO $$
DECLARE
    sample_id constant uuid := '90000000-0000-0000-0000-000000005000';
    source_id uuid := COALESCE(NULLIF(current_setting('bonbon.payment_sample_source', true), '')::uuid,
        '20000000-0000-0000-0000-000000000003'::uuid);
    source bonbon.vehicle%ROWTYPE;
BEGIN
    IF current_database() <> 'bonbon' AND current_database() NOT LIKE 'bonbon_booking_test_%' THEN
        RAISE EXCEPTION 'Refusing seed outside the approved BonBon/demo test database';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('bonbon-payment-sample-5000'));
    IF EXISTS (SELECT 1 FROM bonbon.vehicle WHERE vehicle_id = sample_id) THEN
        IF NOT EXISTS (SELECT 1 FROM bonbon.vehicle WHERE vehicle_id = sample_id AND plate_number = 'BONBON-PAYMENT-TEST') THEN
            RAISE EXCEPTION 'Sample ID already belongs to an unrelated vehicle';
        END IF;
        RAISE NOTICE 'Payment sample already exists; no row or booking price was modified';
        RETURN;
    END IF;
    SELECT * INTO source FROM bonbon.vehicle WHERE vehicle_id = source_id AND status = 'Available' FOR SHARE;
    IF NOT FOUND OR NOT bonbon.has_active_role(source.owner_account_id, 3::smallint) THEN
        RAISE EXCEPTION 'Source vehicle must exist, be available and have an active owner';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM bonbon.vehicle_image WHERE vehicle_id = source_id) THEN
        RAISE EXCEPTION 'Source vehicle has no listing photos';
    END IF;
    INSERT INTO bonbon.vehicle (vehicle_id, owner_account_id, plate_number, brand, model, production_year,
        body_type, seats, transmission, fuel_type, pickup_address, description, features, hour_price, day_price,
        weekly_discount_enabled, weekly_discount_percent, mileage_limit_enabled, daily_mileage_limit_km,
        overage_fee_per_km, rental_terms, status, reviewed_by, reviewed_at)
    VALUES (sample_id, source.owner_account_id, 'BONBON-PAYMENT-TEST', source.brand,
        left(source.model || ' [TEST thanh toan 5K]', 100), source.production_year, source.body_type,
        source.seats, source.transmission, source.fuel_type, source.pickup_address,
        'XE MAU KIEM THU THANH TOAN. Gia 5.000 VND/ngay; khong phuc vu cho thue xe thuc te. Anh duoc dung lai tu xe co san.',
        source.features, 5000, 5000, false, NULL, false, NULL, NULL,
        'Chi dung kiem thu luong dat xe va VNPay Sandbox; khong phai dich vu cho thue thuc te.',
        'Available', source.reviewed_by, now());
    -- Reuse existing image URLs/IDs instead of duplicating WebP bytea data.
    INSERT INTO bonbon.vehicle_image (vehicle_id, image_url, sort_order, is_cover)
    SELECT sample_id, COALESCE(image_url, '/api/vehicles/images/' || vehicle_image_id::text),
        (row_number() OVER (ORDER BY is_cover DESC, sort_order, vehicle_image_id) - 1)::integer,
        row_number() OVER (ORDER BY is_cover DESC, sort_order, vehicle_image_id) = 1
    FROM bonbon.vehicle_image WHERE vehicle_id = source_id ORDER BY is_cover DESC, sort_order, vehicle_image_id LIMIT 8;
END $$;
COMMIT;
