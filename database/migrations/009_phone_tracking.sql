-- Tracking dùng GPS của điện thoại người thuê thay cho thiết bị Traccar gắn trên xe.
DROP INDEX IF EXISTS bonbon.uq_vehicle_traccar_device_id;

ALTER TABLE bonbon.vehicle
    DROP COLUMN IF EXISTS traccar_device_id;

-- Mỗi fix của điện thoại chỉ lưu một lần cho mỗi mốc thời gian.
CREATE UNIQUE INDEX IF NOT EXISTS uq_vehicle_location_phone_fix
    ON bonbon.vehicle_location (booking_id, recorded_at)
    WHERE source = 'Phone';
