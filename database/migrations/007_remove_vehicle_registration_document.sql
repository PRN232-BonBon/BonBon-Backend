BEGIN;

-- Step 3 only collects public vehicle photos. Refuse to discard existing documents.
DO $$
BEGIN
    IF to_regclass('bonbon.vehicle_document') IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM bonbon.vehicle_document) THEN
            RAISE EXCEPTION 'vehicle_document contains data; archive it before removing the table';
        END IF;
        DROP TABLE bonbon.vehicle_document;
    END IF;
END $$;

COMMIT;
