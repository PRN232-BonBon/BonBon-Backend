-- BonBon: PostgreSQL schema v1, revision 3 (Account / Role / AccountRole)
-- Source: current Logical ERD and approved Account / Role / AccountRole model.
-- Target: PostgreSQL 16 or later. Run once against an empty database.
-- Example: psql -X -v ON_ERROR_STOP=1 -d bonbon -f BonBon_PostgreSQL_v1.sql
-- pgAdmin: select the intended database, open Query Tool, execute this file.
-- No database creation, DROP, demo data, users, passwords or permissions here.
-- 14 tables, matching the updated Logical ERD.
-- One Account can hold Admin, Customer and VehicleOwner roles independently.
-- Status lists below are implementation values; coordinate them with the API.

BEGIN;

CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE SCHEMA bonbon;
SET LOCAL search_path = bonbon, public;
SET LOCAL TIME ZONE 'UTC';

-- Currency: VND only. numeric(18,2) maps to C# decimal; CHECK rejects cents
-- and NaN. All timestamps are instants; clients should submit UTC/offset values.

CREATE TABLE account (
    account_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email varchar(254) NOT NULL CHECK (btrim(email) <> ''),
    password_hash varchar(512) NOT NULL CHECK (btrim(password_hash) <> ''),
    full_name varchar(150) NOT NULL CHECK (btrim(full_name) <> ''),
    display_name varchar(150) CHECK (display_name IS NULL OR btrim(display_name) <> ''),
    phone varchar(30) CHECK (phone IS NULL OR btrim(phone) <> ''),
    status varchar(20) NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active', 'Suspended', 'Inactive')),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX uq_account_email ON account (lower(btrim(email)));
-- Password hashes are generated/verified by the API authentication library.
-- Shared identity/contact fields live once in Account. No duplicate actor profiles.

CREATE TABLE role (
    role_id smallint PRIMARY KEY,
    code varchar(30) NOT NULL UNIQUE,
    CONSTRAINT ck_role_code CHECK (
        (role_id = 1 AND code = 'Admin') OR
        (role_id = 2 AND code = 'Customer') OR
        (role_id = 3 AND code = 'VehicleOwner'))
);
-- Fixed role identifiers are reference data, not demo accounts.
INSERT INTO role (role_id, code) VALUES
    (1, 'Admin'), (2, 'Customer'), (3, 'VehicleOwner');

CREATE TABLE account_role (
    account_id uuid NOT NULL REFERENCES account(account_id),
    role_id smallint NOT NULL REFERENCES role(role_id),
    status varchar(20) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending', 'Active', 'Rejected', 'Suspended')),
    assigned_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, role_id)
);
CREATE INDEX ix_account_role_role_status ON account_role (role_id, status);
-- Register Account and Customer membership in one transaction. Owner registration
-- creates Pending membership; approval activates that role. Admin grants are server
-- controlled: public registration must never accept an arbitrary role from a client.
-- Account suspension disables every role; membership suspension affects one role.

CREATE TABLE vehicle (
    vehicle_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    owner_account_id uuid NOT NULL,
    owner_role_id smallint GENERATED ALWAYS AS (3::smallint) STORED,
    CONSTRAINT fk_vehicle_owner_role FOREIGN KEY (owner_account_id, owner_role_id)
        REFERENCES account_role(account_id, role_id),
    plate_number varchar(30) NOT NULL CHECK (btrim(plate_number) <> ''),
    brand varchar(80) NOT NULL CHECK (btrim(brand) <> ''),
    model varchar(100) NOT NULL CHECK (btrim(model) <> ''),
    seats smallint NOT NULL CHECK (seats > 0),
    transmission varchar(20) NOT NULL
        CHECK (transmission IN ('Manual', 'Automatic')),
    pickup_address text NOT NULL CHECK (btrim(pickup_address) <> ''),
    hour_price numeric(18,2) NOT NULL,
    day_price numeric(18,2) NOT NULL,
    status varchar(25) NOT NULL DEFAULT 'Draft'
        CHECK (status IN ('Draft', 'PendingApproval', 'Available', 'Suspended', 'Archived')),
    CONSTRAINT ck_vehicle_hour_price CHECK (
        hour_price > 0 AND hour_price < 10000000000000000
        AND hour_price = trunc(hour_price)),
    CONSTRAINT ck_vehicle_day_price CHECK (
        day_price > 0 AND day_price < 10000000000000000
        AND day_price = trunc(day_price))
);
CREATE UNIQUE INDEX uq_vehicle_plate ON vehicle (upper(btrim(plate_number)));
CREATE INDEX ix_vehicle_owner ON vehicle (owner_account_id);

CREATE TABLE vehicle_image (
    vehicle_image_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    vehicle_id uuid NOT NULL REFERENCES vehicle(vehicle_id),
    image_url text NOT NULL CHECK (btrim(image_url) <> ''),
    sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
    is_cover boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_vehicle_image_vehicle_order ON vehicle_image (vehicle_id, sort_order);
CREATE UNIQUE INDEX uq_vehicle_image_cover ON vehicle_image (vehicle_id) WHERE is_cover;
-- VehicleImage is listing photography, not inspection evidence.
-- Publishing and deleting the last image must be checked in the API transaction.

CREATE TABLE availability_block (
    availability_block_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    vehicle_id uuid NOT NULL REFERENCES vehicle(vehicle_id),
    start_at timestamptz NOT NULL,
    end_at timestamptz NOT NULL,
    reason text,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_availability_block_interval CHECK (
        isfinite(start_at) AND isfinite(end_at) AND start_at < end_at)
);
CREATE INDEX ix_availability_block_range ON availability_block
    USING gist (vehicle_id, tstzrange(start_at, end_at, '[)'));

CREATE TABLE booking (
    booking_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_account_id uuid NOT NULL,
    customer_role_id smallint GENERATED ALWAYS AS (2::smallint) STORED,
    CONSTRAINT fk_booking_customer_role FOREIGN KEY (customer_account_id, customer_role_id)
        REFERENCES account_role(account_id, role_id),
    vehicle_id uuid NOT NULL REFERENCES vehicle(vehicle_id),
    rental_type varchar(10) NOT NULL CHECK (rental_type IN ('Hourly', 'Daily')),
    quantity integer NOT NULL,
    start_at timestamptz NOT NULL,
    end_at timestamptz NOT NULL,
    unit_price_snapshot numeric(18,2) NOT NULL,
    -- ERD retains this field, but the DB calculates it instead of trusting a client.
    quoted_rental_amount numeric(18,2)
        GENERATED ALWAYS AS (quantity * unit_price_snapshot) STORED,
    currency varchar(3) NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    status varchar(30) NOT NULL DEFAULT 'PendingApproval'
        CHECK (status IN (
            'PendingApproval', 'AwaitingPayment', 'Confirmed', 'ReadyForPickup',
            'InProgress', 'Returned', 'AwaitingSettlement', 'Completed',
            'Rejected', 'Cancelled')),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_booking_quantity CHECK (
        (rental_type = 'Hourly' AND quantity >= 4)
        OR (rental_type = 'Daily' AND quantity >= 1)),
    CONSTRAINT ck_booking_interval CHECK (
        isfinite(start_at) AND isfinite(end_at) AND start_at < end_at),
    -- A day is exactly 24 hours, even if the session time zone observes DST.
    CONSTRAINT ck_booking_duration CHECK (
        extract(epoch FROM (end_at - start_at)) = quantity::numeric *
            CASE rental_type WHEN 'Hourly' THEN 3600 ELSE 86400 END),
    CONSTRAINT ck_booking_unit_price CHECK (
        unit_price_snapshot > 0 AND unit_price_snapshot < 10000000000000000
        AND unit_price_snapshot = trunc(unit_price_snapshot)),
    CONSTRAINT uq_booking_vehicle UNIQUE (booking_id, vehicle_id),
    -- Pending requests can overlap; acceptance (AwaitingPayment) claims the slot.
    -- Completed history remains protected; rejection/cancellation releases it.
    CONSTRAINT ex_booking_vehicle_schedule EXCLUDE USING gist (
        vehicle_id WITH =,
        tstzrange(start_at, end_at, '[)') WITH &&
    ) WHERE (status IN (
        'AwaitingPayment', 'Confirmed', 'ReadyForPickup', 'InProgress',
        'Returned', 'AwaitingSettlement', 'Completed'))
);
CREATE INDEX ix_booking_customer_created ON booking (customer_account_id, created_at DESC);
CREATE INDEX ix_booking_vehicle ON booking (vehicle_id);

CREATE TABLE payment (
    payment_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES booking(booking_id),
    provider_reference varchar(200) UNIQUE,
    provider varchar(50) NOT NULL CHECK (btrim(provider) <> ''),
    amount numeric(18,2) NOT NULL CHECK (
        amount > 0 AND amount < 10000000000000000 AND amount = trunc(amount)),
    currency varchar(3) NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    status varchar(20) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending', 'Processing', 'Succeeded', 'Failed', 'Cancelled')),
    paid_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_payment_paid_at CHECK (status <> 'Succeeded' OR paid_at IS NOT NULL)
);
CREATE INDEX ix_payment_booking_created ON payment (booking_id, created_at DESC);
-- Multiple payment attempts are allowed. API verifies gateway results and amounts.

CREATE TABLE wallet (
    wallet_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    role_id smallint NOT NULL CHECK (role_id IN (2, 3)),
    currency varchar(3) NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    status varchar(20) NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active', 'Frozen', 'Closed')),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_wallet_account_role UNIQUE (account_id, role_id),
    CONSTRAINT fk_wallet_account_role FOREIGN KEY (account_id, role_id)
        REFERENCES account_role(account_id, role_id)
);
-- Separate Customer and VehicleOwner wallets; no Admin wallet.

CREATE TABLE wallet_transaction (
    wallet_transaction_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    wallet_id uuid NOT NULL REFERENCES wallet(wallet_id),
    booking_id uuid REFERENCES booking(booking_id),
    idempotency_key varchar(200) NOT NULL UNIQUE CHECK (btrim(idempotency_key) <> ''),
    type varchar(50) NOT NULL CHECK (btrim(type) <> ''),
    direction varchar(10) NOT NULL CHECK (direction IN ('Credit', 'Debit')),
    amount numeric(18,2) NOT NULL CHECK (
        amount > 0 AND amount < 10000000000000000 AND amount = trunc(amount)),
    status varchar(20) NOT NULL DEFAULT 'Pending'
        CHECK (status IN ('Pending', 'Posted', 'Cancelled')),
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_wallet_transaction_wallet_time ON wallet_transaction (wallet_id, occurred_at DESC);
CREATE INDEX ix_wallet_transaction_booking ON wallet_transaction (booking_id);
-- Balance = SUM(Posted Credit) - SUM(Posted Debit). Pending is not spendable.
-- No balance column. API must serialize debits and prevent overspending.
-- Ledger Type, commission, withdrawals and refunds require agreed API rules.

CREATE TABLE message (
    message_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES booking(booking_id),
    sender_account_id uuid NOT NULL,
    sender_role_id smallint NOT NULL CHECK (sender_role_id IN (2, 3)),
    body text NOT NULL CHECK (btrim(body) <> ''),
    sent_at timestamptz NOT NULL DEFAULT now(),
    read_at timestamptz,
    CONSTRAINT fk_message_sender_role FOREIGN KEY (sender_account_id, sender_role_id)
        REFERENCES account_role(account_id, role_id),
    CONSTRAINT ck_message_read_at CHECK (read_at IS NULL OR read_at >= sent_at)
);
CREATE INDEX ix_message_booking_time ON message (booking_id, sent_at, message_id);
CREATE INDEX ix_message_sender_role ON message (sender_account_id, sender_role_id);

CREATE TABLE vehicle_inspection (
    vehicle_inspection_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL,
    vehicle_id uuid NOT NULL REFERENCES vehicle(vehicle_id),
    kind varchar(10) NOT NULL CHECK (kind IN ('Pickup', 'Return')),
    odometer_km numeric(12,1) CHECK (odometer_km >= 0 AND odometer_km < 100000000000),
    fuel_level numeric(5,2) CHECK (fuel_level BETWEEN 0 AND 100),
    condition_notes text,
    inspected_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_vehicle_inspection_kind UNIQUE (booking_id, kind),
    CONSTRAINT fk_vehicle_inspection_booking_vehicle FOREIGN KEY (booking_id, vehicle_id)
        REFERENCES booking(booking_id, vehicle_id)
);
CREATE INDEX ix_vehicle_inspection_vehicle ON vehicle_inspection (vehicle_id);
-- FuelLevel uses percentage 0..100. Odometer is optional, not a km billing input.

CREATE TABLE additional_charge (
    additional_charge_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES booking(booking_id),
    reason_code varchar(50) NOT NULL CHECK (btrim(reason_code) <> ''),
    description text NOT NULL CHECK (btrim(description) <> ''),
    amount numeric(18,2) NOT NULL CHECK (
        amount > 0 AND amount < 10000000000000000 AND amount = trunc(amount)),
    currency varchar(3) NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    evidence_url text,
    status varchar(20) NOT NULL DEFAULT 'Proposed'
        CHECK (status IN ('Proposed', 'Accepted', 'Rejected', 'Paid', 'Cancelled')),
    proposed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_additional_charge_booking_time ON additional_charge (booking_id, proposed_at DESC);
-- Separate from the original quote. No automatic late/km fee formula is defined.

CREATE TABLE vehicle_location (
    vehicle_location_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    booking_id uuid NOT NULL REFERENCES booking(booking_id),
    latitude numeric(9,6) NOT NULL CHECK (latitude BETWEEN -90 AND 90),
    longitude numeric(10,6) NOT NULL CHECK (longitude BETWEEN -180 AND 180),
    accuracy_meters numeric(10,2) CHECK (accuracy_meters >= 0 AND accuracy_meters < 100000000),
    source varchar(50) NOT NULL CHECK (btrim(source) <> ''),
    recorded_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_vehicle_location_booking_time ON vehicle_location (booking_id, recorded_at DESC);
-- API accepts new locations only while InProgress. A phone location is not proof
-- of the vehicle's physical position. Access and retention are API responsibilities.

-- Eligibility is checked when an action is created, not retroactively on history.
CREATE FUNCTION bonbon.has_active_role(p_account_id uuid, p_role_id smallint)
RETURNS boolean LANGUAGE sql STABLE SET search_path = bonbon, public AS $$
    SELECT EXISTS (
        SELECT 1 FROM bonbon.account a JOIN bonbon.account_role ar USING (account_id)
        WHERE a.account_id = p_account_id AND ar.role_id = p_role_id
          AND a.status = 'Active' AND ar.status = 'Active');
$$;

CREATE FUNCTION bonbon.check_actor_eligibility()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
DECLARE selected_owner uuid;
BEGIN
    IF TG_TABLE_NAME = 'vehicle' THEN
        IF NEW.status = 'Available' AND NOT bonbon.has_active_role(NEW.owner_account_id, 3::smallint) THEN
            RAISE EXCEPTION 'Available vehicle requires an active VehicleOwner and Account'
                USING ERRCODE = '23514';
        END IF;
    ELSIF TG_TABLE_NAME = 'booking' THEN
        IF NOT bonbon.has_active_role(NEW.customer_account_id, 2::smallint) THEN
            RAISE EXCEPTION 'Active Customer role and Account are required' USING ERRCODE = '23514';
        END IF;
        SELECT owner_account_id INTO selected_owner FROM bonbon.vehicle WHERE vehicle_id = NEW.vehicle_id;
        IF NOT bonbon.has_active_role(selected_owner, 3::smallint) THEN
            RAISE EXCEPTION 'Active VehicleOwner role and Account are required' USING ERRCODE = '23514';
        END IF;
    ELSE
        IF NOT bonbon.has_active_role(NEW.account_id, NEW.role_id) THEN
            RAISE EXCEPTION 'Wallet requires an active rental role and Account' USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER vehicle_actor_guard BEFORE INSERT OR UPDATE OF owner_account_id, status ON vehicle
FOR EACH ROW EXECUTE FUNCTION bonbon.check_actor_eligibility();
CREATE TRIGGER booking_actor_guard BEFORE INSERT OR UPDATE OF customer_account_id, vehicle_id ON booking
FOR EACH ROW EXECUTE FUNCTION bonbon.check_actor_eligibility();
CREATE TRIGGER wallet_actor_guard BEFORE INSERT OR UPDATE OF account_id, role_id ON wallet
FOR EACH ROW EXECUTE FUNCTION bonbon.check_actor_eligibility();

-- A sender must be the actual Customer/VehicleOwner in the selected Booking.
CREATE FUNCTION bonbon.check_message_participant()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
DECLARE expected_customer uuid; expected_owner uuid;
BEGIN
    SELECT b.customer_account_id, v.owner_account_id INTO expected_customer, expected_owner
    FROM bonbon.booking b JOIN bonbon.vehicle v ON v.vehicle_id = b.vehicle_id
    WHERE b.booking_id = NEW.booking_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Message booking does not exist' USING ERRCODE = '23503';
    END IF;
    IF NOT ((NEW.sender_role_id = 2 AND NEW.sender_account_id = expected_customer)
        OR (NEW.sender_role_id = 3 AND NEW.sender_account_id = expected_owner)) THEN
        RAISE EXCEPTION 'Message sender/role is not a participant of this booking'
            USING ERRCODE = '23514';
    END IF;
    IF NOT bonbon.has_active_role(NEW.sender_account_id, NEW.sender_role_id) THEN
        RAISE EXCEPTION 'Message sender Account and role must be active' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER message_participant_guard
BEFORE INSERT OR UPDATE OF booking_id, sender_account_id, sender_role_id ON message
FOR EACH ROW EXECUTE FUNCTION bonbon.check_message_participant();

-- Historical references remain stable. A rental vehicle's ownership is not
-- transferred by editing OwnerAccountId after bookings have been recorded.
CREATE FUNCTION bonbon.protect_vehicle_owner()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
BEGIN
    IF NEW.owner_account_id IS DISTINCT FROM OLD.owner_account_id
       AND EXISTS (SELECT 1 FROM bonbon.booking WHERE vehicle_id = OLD.vehicle_id) THEN
        RAISE EXCEPTION 'Vehicle owner cannot change after a booking exists'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER vehicle_owner_history_guard
BEFORE UPDATE OF owner_account_id ON vehicle
FOR EACH ROW EXECUTE FUNCTION bonbon.protect_vehicle_owner();

CREATE FUNCTION bonbon.protect_booking_quote()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
BEGIN
    IF NEW.customer_account_id IS DISTINCT FROM OLD.customer_account_id
       OR NEW.vehicle_id IS DISTINCT FROM OLD.vehicle_id THEN
        RAISE EXCEPTION 'Booking participants cannot be replaced'
            USING ERRCODE = '23514';
    END IF;
    IF OLD.status <> 'PendingApproval' AND NEW.status = 'PendingApproval' THEN
        RAISE EXCEPTION 'Booking cannot return to PendingApproval after a decision'
            USING ERRCODE = '23514';
    END IF;
    IF OLD.status <> 'PendingApproval' AND (
        NEW.rental_type IS DISTINCT FROM OLD.rental_type
        OR NEW.quantity IS DISTINCT FROM OLD.quantity
        OR NEW.start_at IS DISTINCT FROM OLD.start_at
        OR NEW.end_at IS DISTINCT FROM OLD.end_at
        OR NEW.unit_price_snapshot IS DISTINCT FROM OLD.unit_price_snapshot) THEN
        RAISE EXCEPTION 'Accepted booking time and price are immutable'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER booking_quote_guard BEFORE UPDATE ON booking
FOR EACH ROW EXECUTE FUNCTION bonbon.protect_booking_quote();

-- Serialize schedule writes for one Vehicle at the default READ COMMITTED
-- isolation level. Booking-vs-Booking is additionally protected by EXCLUDE.
-- Repeatable Read is rejected here because its old snapshot could miss a new
-- block after waiting for the vehicle lock. Serializable is allowed; retry 40001.
CREATE FUNCTION bonbon.check_vehicle_schedule()
RETURNS trigger LANGUAGE plpgsql SET search_path = bonbon, public AS $$
BEGIN
    IF current_setting('transaction_isolation') = 'repeatable read' THEN
        RAISE EXCEPTION 'Schedule writes require Read Committed or Serializable'
            USING ERRCODE = '25000';
    END IF;
    PERFORM 1 FROM bonbon.vehicle WHERE vehicle_id = NEW.vehicle_id FOR UPDATE;
    IF TG_TABLE_NAME = 'booking' THEN
        IF NEW.status NOT IN ('Rejected', 'Cancelled') AND EXISTS (
            SELECT 1 FROM bonbon.availability_block ab
            WHERE ab.vehicle_id = NEW.vehicle_id
              AND tstzrange(ab.start_at, ab.end_at, '[)') &&
                  tstzrange(NEW.start_at, NEW.end_at, '[)')) THEN
            RAISE EXCEPTION 'Booking overlaps an availability block'
                USING ERRCODE = '23P01';
        END IF;
    ELSE
        IF EXISTS (
            SELECT 1 FROM bonbon.booking b
            WHERE b.vehicle_id = NEW.vehicle_id
              AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
                  'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
              AND tstzrange(b.start_at, b.end_at, '[)') &&
                  tstzrange(NEW.start_at, NEW.end_at, '[)')) THEN
            RAISE EXCEPTION 'Availability block overlaps a reserved booking'
                USING ERRCODE = '23P01';
        END IF;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER booking_schedule_guard
BEFORE INSERT OR UPDATE OF vehicle_id, start_at, end_at, status ON booking
FOR EACH ROW EXECUTE FUNCTION bonbon.check_vehicle_schedule();
CREATE TRIGGER availability_block_schedule_guard
BEFORE INSERT OR UPDATE OF vehicle_id, start_at, end_at ON availability_block
FOR EACH ROW EXECUTE FUNCTION bonbon.check_vehicle_schedule();

-- Optional entry point for API code: server chooses price from Vehicle, computes
-- EndAt, and inserts PendingApproval. It does not accept a client-supplied price.
CREATE FUNCTION bonbon.create_booking(
    p_customer_account_id uuid,
    p_vehicle_id uuid,
    p_rental_type varchar,
    p_quantity integer,
    p_start_at timestamptz
) RETURNS bonbon.booking LANGUAGE plpgsql SET search_path = bonbon, public AS $$
DECLARE
    selected_vehicle bonbon.vehicle%ROWTYPE;
    result bonbon.booking%ROWTYPE;
    selected_price numeric(18,2);
    selected_end timestamptz;
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
    IF p_start_at IS NULL OR NOT isfinite(p_start_at) OR p_quantity IS NULL
       OR p_rental_type IS NULL THEN
        RAISE EXCEPTION 'Rental type, quantity and finite start time are required'
            USING ERRCODE = '23514';
    END IF;
    IF p_rental_type = 'Hourly' AND p_quantity >= 4 THEN
        selected_price := selected_vehicle.hour_price;
        selected_end := p_start_at + p_quantity * interval '1 hour';
    ELSIF p_rental_type = 'Daily' AND p_quantity >= 1 THEN
        selected_price := selected_vehicle.day_price;
        selected_end := p_start_at + p_quantity * interval '24 hours';
    ELSE
        RAISE EXCEPTION 'Hourly requires at least 4 hours; Daily requires at least 1 day'
            USING ERRCODE = '23514';
    END IF;
    IF EXISTS (
        SELECT 1 FROM bonbon.booking b WHERE b.vehicle_id = p_vehicle_id
          AND b.status IN ('AwaitingPayment', 'Confirmed', 'ReadyForPickup',
              'InProgress', 'Returned', 'AwaitingSettlement', 'Completed')
          AND tstzrange(b.start_at, b.end_at, '[)') &&
              tstzrange(p_start_at, selected_end, '[)')) THEN
        RAISE EXCEPTION 'Vehicle already reserved for this period' USING ERRCODE = '23P01';
    END IF;
    INSERT INTO bonbon.booking (
        customer_account_id, vehicle_id, rental_type, quantity, start_at, end_at, unit_price_snapshot
    ) VALUES (
        p_customer_account_id, p_vehicle_id, p_rental_type, p_quantity,
        p_start_at, selected_end, selected_price
    ) RETURNING * INTO result;
    RETURN result;
END;
$$;

COMMENT ON TABLE bonbon.account IS 'Single platform login and shared identity/contact data.';
COMMENT ON COLUMN bonbon.account.password_hash IS 'Encoded hash generated by the authentication library; never plaintext.';
COMMENT ON TABLE bonbon.role IS 'Fixed roles: 1 Admin, 2 Customer, 3 VehicleOwner.';
COMMENT ON TABLE bonbon.account_role IS 'Per-role membership lifecycle; one Account may hold multiple roles.';
COMMENT ON COLUMN bonbon.vehicle.owner_role_id IS 'Generated constant 3; composite FK enforces VehicleOwner membership.';
COMMENT ON COLUMN bonbon.booking.customer_role_id IS 'Generated constant 2; composite FK enforces Customer membership.';
COMMENT ON TABLE bonbon.wallet IS 'At most one wallet per Account and rental role; Admin excluded.';
COMMENT ON TABLE bonbon.message IS 'SenderAccountId and SenderRoleId reference membership and identify booking participant context.';
COMMENT ON TABLE bonbon.booking IS 'Hourly/Daily rental; Quantity defines hours/days, quote excludes additional charges.';
COMMENT ON COLUMN bonbon.vehicle.hour_price IS 'VND per hour. 4h and 8h are UI presets only.';
COMMENT ON COLUMN bonbon.vehicle.day_price IS 'VND per day of exactly 24 hours.';
COMMENT ON COLUMN bonbon.booking.unit_price_snapshot IS 'Price selected at booking; independent of later Vehicle price edits.';
COMMENT ON COLUMN bonbon.booking.quoted_rental_amount IS 'Generated: Quantity * UnitPriceSnapshot. Omit from INSERT/UPDATE.';
COMMENT ON COLUMN bonbon.booking.end_at IS 'Booked return deadline, not the actual return time.';
COMMENT ON COLUMN bonbon.vehicle_inspection.fuel_level IS 'Optional fuel percentage, 0..100.';
COMMENT ON COLUMN bonbon.vehicle_inspection.odometer_km IS 'Optional evidence only; no mileage-based billing in demo.';

-- API responsibilities before a complete application exists:
-- 1. Register/login using Account; resolve active roles from AccountRole.
--    Validate authenticated AccountId and selected actor role on every request.
--    Public clients cannot grant Admin or approve VehicleOwner membership.
--    OwnerRoleId/CustomerRoleId are generated: omit them from INSERT/UPDATE.
--    Hash verification, access tokens, email verification and reset-password flows
--    belong to the authentication implementation, not to this SQL file.
-- 2. Validate current actor, approved vehicle/images and eligible customer.
-- 3. Verify real payment notifications and legal status transitions; never mark
--    Confirmed merely because a client submits that status.
-- 4. Pickup/Return workflow, location permissions, additional-charge approval,
--    refunds and settlement. This script does not invent their pricing policies.
-- 5. Idempotent ledger posting and debit locking; posted entries must be immutable
--    in the API (this script does not yet enforce ledger immutability).
--    Wallet ownership and role are immutable in API once ledger entries exist.
--    Role/account suspension must also be checked for payments, debits and admin
--    actions; insert-time eligibility triggers are not full API authorization.
-- 6. EF Core/Npgsql: Guid -> uuid; decimal -> numeric; UTC DateTime -> timestamptz;
--    generated QuotedRentalAmount is database-generated. Use bonbon schema.
-- 7. Exclusion violations: SQLSTATE 23P01; duplicates: 23505; retry transaction
--    conflicts/deadlocks: 40001/40P01. Adjacent [start,end) bookings are allowed.
-- Foreign keys default to NO ACTION: historical rows cannot disappear by cascade.

COMMIT;
