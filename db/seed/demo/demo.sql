-- =============================================================================
-- Dữ liệu demo cho buổi bảo vệ và E2E (docs/08-deployment.md, Dữ liệu demo). KHÔNG chạy ở production thật.
-- Chạy bằng: dotnet run --project apps/api/src/TroViec.Migrator -- seed-demo
-- Idempotent: chạy lại không tạo trùng. {{PASSWORD_HASH}} và {{STUDENT_DOMAIN}} do lệnh seed-demo thay vào.
-- Mật khẩu chung của mọi tài khoản demo nằm trong db/seed/demo/README.md.
-- =============================================================================

SET search_path = app, public;

-- ---- Tài khoản (đã xác thực email + số điện thoại) ---------------------------
CREATE TEMP TABLE demo_accounts (email text, full_name text, phone text, roles text[]) ON COMMIT DROP;
INSERT INTO demo_accounts VALUES
    ('sinhvien@{{STUDENT_DOMAIN}}',   'Nguyễn Văn An (demo)',  '+84900000001', ARRAY['student']),
    ('sinhvien2@{{STUDENT_DOMAIN}}',  'Trần Thị Bình (demo)',  '+84900000002', ARRAY['student']),
    ('chutro@demo.troviec.test',      'Lê Văn Cường (demo)',   '+84900000003', ARRAY['owner']),
    ('chutro2@demo.troviec.test',     'Phạm Thị Dung (demo)',  '+84900000004', ARRAY['owner']),
    ('canbo@demo.troviec.test',       'Hoàng Văn Em (demo)',   '+84900000005', ARRAY['moderator']),
    ('canbo2@demo.troviec.test',      'Vũ Thị Phương (demo)',  '+84900000006', ARRAY['moderator']),
    ('quantri@demo.troviec.test',     'Đỗ Văn Giang (demo)',   '+84900000007', ARRAY['admin']);

INSERT INTO app.users (email, password_hash, full_name, email_verified_at, phone_e164, phone_verified_at)
SELECT a.email, '{{PASSWORD_HASH}}', a.full_name, now(), a.phone, now()
FROM demo_accounts a
WHERE NOT EXISTS (SELECT 1 FROM app.users u WHERE lower(u.email) = lower(a.email));

INSERT INTO app.user_roles (user_id, role_id, granter_id)
SELECT u.id, r.id, app.system_user_id()
FROM demo_accounts a
JOIN app.users u ON lower(u.email) = lower(a.email)
JOIN app.roles r ON r.code = ANY(a.roles)
ON CONFLICT DO NOTHING;

-- Hồ sơ chủ đã xác minh (BR-02) cho hai chủ trọ demo.
INSERT INTO app.owner_profiles (user_id, owner_type, business_name, tax_code, verified_until, verifier_id)
SELECT u.id, 'individual', NULL, NULL, now() + interval '1 year',
       (SELECT id FROM app.users WHERE email = 'canbo@demo.troviec.test')
FROM app.users u
WHERE u.email IN ('chutro@demo.troviec.test', 'chutro2@demo.troviec.test')
ON CONFLICT (user_id) DO NOTHING;

-- ---- 20 tin phòng + 10 tin việc đã xác minh quanh cơ sở đầu tiên ------------
-- Chỉ chạy khi chủ trọ demo chưa có tin (idempotent). Tọa độ rải quanh (21.0537, 105.7353).
DO $$
DECLARE
    owner1 bigint := (SELECT id FROM app.users WHERE email = 'chutro@demo.troviec.test');
    owner2 bigint := (SELECT id FROM app.users WHERE email = 'chutro2@demo.troviec.test');
    ward   text   := (SELECT code FROM app.administrative_units WHERE unit_level = 'commune' ORDER BY code LIMIT 1);
    lid    bigint;
    i      int;
    streets text[] := ARRAY['Cầu Diễn', 'Hồ Tùng Mậu', 'Xuân Thủy', 'Phạm Văn Đồng', 'Nguyễn Hoàng', 'Mỹ Đình',
                            'Trần Thái Tông', 'Hoàng Quốc Việt', 'Phúc Diễn', 'Kiều Mai'];
BEGIN
    IF owner1 IS NULL OR ward IS NULL OR EXISTS (SELECT 1 FROM app.listings WHERE poster_id IN (owner1, owner2)) THEN
        RETURN;
    END IF;

    FOR i IN 1..20 LOOP
        INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
        VALUES (
            CASE WHEN i % 2 = 0 THEN owner2 ELSE owner1 END, 'room',
            format('Phòng trọ %s m² gần ĐH, đường %s', 15 + i, streets[1 + i % 10]),
            format('Phòng %s m² %s, có điều hòa, nóng lạnh, giờ giấc tự do. Gần trường đại học, chợ và bến xe buýt. '
                   'Điện nước theo giá nhà nước, xem phòng trực tiếp trước khi đặt cọc.',
                   15 + i, CASE WHEN i % 3 = 0 THEN 'khép kín' ELSE 'chung vệ sinh' END),
            ward, format('Số %s ngõ %s đường %s', i * 3, 10 + i, streets[1 + i % 10]),
            ST_SetSRID(ST_MakePoint(105.7353 + (i % 7 - 3) * 0.004, 21.0537 + (i % 5 - 2) * 0.004), 4326)::geography,
            'verified', now() + make_interval(days => 30 + i))
        RETURNING id INTO lid;
        INSERT INTO app.room_listings (listing_id, monthly_rent_vnd, deposit_vnd, area_m2, is_self_contained)
        VALUES (lid, 1500000 + i * 150000, CASE WHEN i % 4 = 0 THEN 0 ELSE 1500000 END, 15 + i, i % 3 = 0);
        INSERT INTO app.room_amenities (listing_id, amenity_id)
        SELECT lid, a.id FROM app.amenities a
        WHERE a.is_active AND a.code = ANY(CASE WHEN i % 2 = 0 THEN ARRAY['wifi', 'parking', 'air_conditioner']
                                                ELSE ARRAY['wifi', 'water_heater'] END);
        INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) VALUES
            (lid, NULL, 'draft', CASE WHEN i % 2 = 0 THEN owner2 ELSE owner1 END),
            (lid, 'pending_review', 'verified', (SELECT id FROM app.users WHERE email = 'canbo@demo.troviec.test'));
        INSERT INTO app.outbox_messages (listing_id) VALUES (lid);   -- embedding by worker-embed
    END LOOP;

    FOR i IN 1..10 LOOP
        INSERT INTO app.listings (poster_id, listing_type, title, description, ward_code, street_address, location, status, expires_at)
        VALUES (
            owner1, 'job',
            (ARRAY['Phục vụ quán cà phê ca tối', 'Gia sư Toán lớp 9', 'Bán hàng siêu thị cuối tuần', 'Pha chế trà sữa',
                   'Trợ giảng tiếng Anh', 'Nhân viên thư viện', 'Thu ngân nhà sách', 'Phụ bếp nhà hàng',
                   'Lễ tân phòng gym', 'Nhập liệu tại văn phòng'])[i],
            'Làm tại địa điểm ghi trong tin, có hợp đồng thời vụ, trả lương theo tuần. Không thu bất kỳ khoản phí nào '
            'khi nhận việc. Phỏng vấn trực tiếp tại cửa hàng.',
            ward, format('Số %s đường %s', i * 7, streets[i]),
            ST_SetSRID(ST_MakePoint(105.7353 + (i % 4 - 2) * 0.005, 21.0537 + (i % 3 - 1) * 0.005), 4326)::geography,
            'verified', now() + make_interval(days => 40 + i))
        RETURNING id INTO lid;
        INSERT INTO app.job_listings (listing_id, employer_name, pay_vnd, pay_unit, hours_per_week)
        VALUES (lid, format('Cửa hàng demo %s', i), 22000 + i * 1000, 'hour', 12 + i);
        INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id) VALUES
            (lid, NULL, 'draft', owner1),
            (lid, 'pending_review', 'verified', (SELECT id FROM app.users WHERE email = 'canbo@demo.troviec.test'));
        INSERT INTO app.outbox_messages (listing_id) VALUES (lid);
    END LOOP;
END $$;
