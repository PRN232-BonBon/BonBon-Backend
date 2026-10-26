BEGIN;

INSERT INTO bonbon.account (
    account_id, email, password_hash, full_name, display_name, status
)
VALUES (
    '10000000-0000-0000-0000-000000000001',
    'demo-owner@bonbon.local',
    NULL,
    'BonBon Demo Fleet',
    'BonBon Demo Fleet',
    'Active'
)
ON CONFLICT (account_id) DO UPDATE SET
    full_name = EXCLUDED.full_name,
    display_name = EXCLUDED.display_name,
    status = EXCLUDED.status;

INSERT INTO bonbon.account_role (account_id, role_id, status)
VALUES ('10000000-0000-0000-0000-000000000001', 3, 'Active')
ON CONFLICT (account_id, role_id) DO UPDATE SET status = EXCLUDED.status;

INSERT INTO bonbon.vehicle (
    vehicle_id, owner_account_id, plate_number, brand, model, production_year,
    body_type, seats, transmission, fuel_type, pickup_address,
    hour_price, day_price, description, features, status
)
VALUES
(
    '20000000-0000-0000-0000-000000000001',
    '10000000-0000-0000-0000-000000000001',
    'DEMO-ACCENT-24', 'Hyundai', 'Accent', 2024,
    'Sedan', 5, 'Automatic', 'Gasoline', 'Phường An Phú, TP. Thủ Đức, TP. Hồ Chí Minh',
    120000, 761000,
    'Sedan 5 chỗ hiện đại, vận hành êm và tiết kiệm nhiên liệu, phù hợp đi phố hoặc những chuyến đi ngắn cuối tuần.',
    ARRAY['Bluetooth', 'Camera lùi', 'Khe cắm USB', 'Điều hòa', 'Túi khí an toàn'],
    'Available'
),
(
    '20000000-0000-0000-0000-000000000002',
    '10000000-0000-0000-0000-000000000001',
    'DEMO-AVANZA-22', 'Toyota', 'Avanza', 2022,
    'MPV', 7, 'Automatic', 'Gasoline', 'Phường Thảo Điền, TP. Thủ Đức, TP. Hồ Chí Minh',
    160000, 915000,
    'MPV 7 chỗ rộng rãi với cách bố trí ghế linh hoạt, phù hợp gia đình và nhóm bạn cần nhiều không gian hành lý.',
    ARRAY['Bluetooth', 'Camera lùi', 'Khe cắm USB', 'Điều hòa sau', 'Túi khí an toàn'],
    'Available'
),
(
    '20000000-0000-0000-0000-000000000003',
    '10000000-0000-0000-0000-000000000001',
    'DEMO-VIOS-26', 'Toyota', 'Vios', 2026,
    'Sedan', 5, 'Automatic', 'Gasoline', 'Phường 15, Quận Bình Thạnh, TP. Hồ Chí Minh',
    120000, 679000,
    'Sedan phổ thông dễ lái, khoang hành khách thoải mái và chi phí vận hành hợp lý cho nhu cầu di chuyển hằng ngày.',
    ARRAY['Bluetooth', 'Camera lùi', 'Khe cắm USB', 'Điều hòa', 'Túi khí an toàn'],
    'Available'
),
(
    '20000000-0000-0000-0000-000000000004',
    '10000000-0000-0000-0000-000000000001',
    'DEMO-VF3-26', 'VinFast', 'VF 3', 2026,
    'Mini SUV', 4, 'Automatic', 'Electric', 'Phường Bến Thành, Quận 1, TP. Hồ Chí Minh',
    90000, 555000,
    'Mẫu xe điện đô thị nhỏ gọn, dễ xoay trở và đỗ xe, phù hợp các chuyến đi ngắn trong thành phố.',
    ARRAY['Màn hình giải trí', 'Khe cắm USB', 'Điều hòa', 'Camera lùi', 'Phanh ABS'],
    'Available'
),
(
    '20000000-0000-0000-0000-000000000005',
    '10000000-0000-0000-0000-000000000001',
    'DEMO-VF8P-24', 'VinFast', 'VF 8 Plus', 2024,
    'SUV', 5, 'Automatic', 'Electric', 'Phường 8, Quận Gò Vấp, TP. Hồ Chí Minh',
    220000, 1340000,
    'SUV điện 5 chỗ rộng rãi, tăng tốc mượt và nhiều tiện nghi, phù hợp cả di chuyển nội đô lẫn hành trình dài.',
    ARRAY['Bluetooth', 'Camera 360', 'Định vị GPS', 'Khe cắm USB', 'Túi khí an toàn'],
    'Available'
)
ON CONFLICT (vehicle_id) DO UPDATE SET
    owner_account_id = EXCLUDED.owner_account_id,
    plate_number = EXCLUDED.plate_number,
    brand = EXCLUDED.brand,
    model = EXCLUDED.model,
    production_year = EXCLUDED.production_year,
    body_type = EXCLUDED.body_type,
    seats = EXCLUDED.seats,
    transmission = EXCLUDED.transmission,
    fuel_type = EXCLUDED.fuel_type,
    pickup_address = EXCLUDED.pickup_address,
    hour_price = EXCLUDED.hour_price,
    day_price = EXCLUDED.day_price,
    description = EXCLUDED.description,
    features = EXCLUDED.features,
    status = EXCLUDED.status;

DELETE FROM bonbon.vehicle_image
WHERE vehicle_id IN (
    '20000000-0000-0000-0000-000000000001',
    '20000000-0000-0000-0000-000000000002',
    '20000000-0000-0000-0000-000000000003',
    '20000000-0000-0000-0000-000000000004',
    '20000000-0000-0000-0000-000000000005'
);

INSERT INTO bonbon.vehicle_image (vehicle_id, image_url, sort_order, is_cover)
VALUES
('20000000-0000-0000-0000-000000000001', '/assets/cars/hyundai-accent-2024-1.webp', 0, true),
('20000000-0000-0000-0000-000000000001', '/assets/cars/hyundai-accent-2024-2.webp', 1, false),
('20000000-0000-0000-0000-000000000001', '/assets/cars/hyundai-accent-2024-3.webp', 2, false),
('20000000-0000-0000-0000-000000000002', '/assets/cars/toyota-avanza-2022-1.webp', 0, true),
('20000000-0000-0000-0000-000000000002', '/assets/cars/toyota-avanza-2022-2.webp', 1, false),
('20000000-0000-0000-0000-000000000002', '/assets/cars/toyota-avanza-2022-3.webp', 2, false),
('20000000-0000-0000-0000-000000000003', '/assets/cars/toyota-vios-2026-1.webp', 0, true),
('20000000-0000-0000-0000-000000000003', '/assets/cars/toyota-vios-2026-2.webp', 1, false),
('20000000-0000-0000-0000-000000000003', '/assets/cars/toyota-vios-2026-3.webp', 2, false),
('20000000-0000-0000-0000-000000000004', '/assets/cars/vinfast-vf3-2026-1.webp', 0, true),
('20000000-0000-0000-0000-000000000004', '/assets/cars/vinfast-vf3-2026-2.webp', 1, false),
('20000000-0000-0000-0000-000000000004', '/assets/cars/vinfast-vf3-2026-3.webp', 2, false),
('20000000-0000-0000-0000-000000000004', '/assets/cars/vinfast-vf3-2026-4.webp', 3, false),
('20000000-0000-0000-0000-000000000005', '/assets/cars/vinfast-vf8-plus-2024-1.webp', 0, true),
('20000000-0000-0000-0000-000000000005', '/assets/cars/vinfast-vf8-plus-2024-2.webp', 1, false),
('20000000-0000-0000-0000-000000000005', '/assets/cars/vinfast-vf8-plus-2024-3.webp', 2, false);

COMMIT;
