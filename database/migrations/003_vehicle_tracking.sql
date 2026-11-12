-- Map each rental vehicle to its Traccar Server device.
ALTER TABLE bonbon.vehicle
    ADD COLUMN traccar_device_id bigint;

ALTER TABLE bonbon.booking
    ADD COLUMN pickup_meeting_latitude numeric(9,6),
    ADD COLUMN pickup_meeting_longitude numeric(10,6),
    ADD COLUMN return_meeting_latitude numeric(9,6),
    ADD COLUMN return_meeting_longitude numeric(10,6),
    ADD CONSTRAINT ck_booking_pickup_meeting_coordinates CHECK (
        num_nonnulls(pickup_meeting_latitude, pickup_meeting_longitude) = 0
        OR (num_nonnulls(pickup_meeting_latitude, pickup_meeting_longitude) = 2
            AND pickup_meeting_latitude BETWEEN -90 AND 90 AND pickup_meeting_longitude BETWEEN -180 AND 180)),
    ADD CONSTRAINT ck_booking_return_meeting_coordinates CHECK (
        num_nonnulls(return_meeting_latitude, return_meeting_longitude) = 0
        OR (num_nonnulls(return_meeting_latitude, return_meeting_longitude) = 2
            AND return_meeting_latitude BETWEEN -90 AND 90 AND return_meeting_longitude BETWEEN -180 AND 180));

CREATE UNIQUE INDEX uq_vehicle_traccar_device_id
    ON bonbon.vehicle (traccar_device_id)
    WHERE traccar_device_id IS NOT NULL;

CREATE UNIQUE INDEX uq_vehicle_location_traccar_fix
    ON bonbon.vehicle_location (booking_id, recorded_at)
    WHERE source = 'Traccar';
