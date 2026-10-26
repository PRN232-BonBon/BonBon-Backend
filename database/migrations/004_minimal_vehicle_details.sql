BEGIN;

ALTER TABLE bonbon.vehicle
    ADD COLUMN IF NOT EXISTS production_year smallint,
    ADD COLUMN IF NOT EXISTS fuel_type varchar(20),
    ADD COLUMN IF NOT EXISTS body_type varchar(30),
    ADD COLUMN IF NOT EXISTS description text,
    ADD COLUMN IF NOT EXISTS features text[] NOT NULL DEFAULT '{}';

ALTER TABLE bonbon.vehicle
    DROP CONSTRAINT IF EXISTS ck_vehicle_production_year,
    DROP CONSTRAINT IF EXISTS ck_vehicle_fuel_type;

ALTER TABLE bonbon.vehicle
    ADD CONSTRAINT ck_vehicle_production_year
        CHECK (production_year IS NULL OR production_year BETWEEN 1990 AND 2100),
    ADD CONSTRAINT ck_vehicle_fuel_type
        CHECK (fuel_type IS NULL OR fuel_type IN ('Gasoline', 'Diesel', 'Electric', 'Hybrid'));

COMMENT ON COLUMN bonbon.vehicle.production_year IS 'Model year shown in the rental catalog.';
COMMENT ON COLUMN bonbon.vehicle.fuel_type IS 'Normalized fuel type for display and filtering.';
COMMENT ON COLUMN bonbon.vehicle.body_type IS 'Simple catalog category such as Sedan, SUV, MPV or Mini SUV.';
COMMENT ON COLUMN bonbon.vehicle.description IS 'Short customer-facing vehicle description.';
COMMENT ON COLUMN bonbon.vehicle.features IS 'Small display-oriented list of vehicle conveniences.';

COMMIT;
