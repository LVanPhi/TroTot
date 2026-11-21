-- =============================================================================
-- 0003_review_fixes.sql — sửa lược đồ sau đợt rà soát tài liệu (11/2026)
--   1. assessments.price_vnd: giá thuê đã dùng để so với trung vị. Trước đây giá trích từ
--      chữ không được lưu ở đâu, nên giao diện không hiện được "Giá nêu X ₫ thấp hơn Y%".
--   2. Vòng đời tin: thêm cạnh expired -> draft để sửa tin đã hết hạn rồi gửi duyệt lại
--      (trước đây phải gia hạn, chờ duyệt, gỡ về nháp, sửa, rồi duyệt lần hai).
--   3. Ngừng dùng tiện nghi private_bathroom: trùng nghĩa room_listings.is_self_contained.
-- Chạy bằng role app_migrator. KHÔNG sửa file này sau khi đã chạy ở bất kỳ môi trường nào.
-- =============================================================================

SET search_path = app, public;

-- -----------------------------------------------------------------------------
-- 1. Giá đã dùng để so (worker ghi cùng price_signal)
-- -----------------------------------------------------------------------------
ALTER TABLE app.assessments
    ADD COLUMN price_vnd INTEGER;  -- NULL: không có tín hiệu giá (price_signal NULL)

-- Dạng một chiều để dòng cũ (price_vnd NULL) vẫn hợp lệ; worker luôn ghi price_vnd khi có
-- price_signal (kiểm bằng test của worker).
ALTER TABLE app.assessments
    ADD CONSTRAINT ck_assessments_price_vnd CHECK (
        price_vnd IS NULL
        OR (price_signal IS NOT NULL AND price_vnd BETWEEN 100000 AND 100000000));

COMMENT ON COLUMN app.assessments.price_vnd IS
    'Giá thuê VND/tháng đã so với trung vị: monthly_rent_vnd (lượt listing), claimed_rent_vnd hoặc giá trích từ chữ (kiểm tra nhanh). NULL khi price_signal NULL.';

-- Quyền mức bảng cấp ở 0001 đã phủ cột mới; cấp lại tường minh theo quy tắc migration số 2.
GRANT SELECT, INSERT ON app.assessments TO app_worker;
GRANT SELECT ON app.assessments TO app_api;

-- -----------------------------------------------------------------------------
-- 2. Vòng đời tin: thêm expired -> draft (chỉ nới thêm cạnh; dòng cũ không bị ảnh hưởng)
-- Danh sách cạnh phải khớp ListingLifecycle.IsAllowed trong API (docs/01-requirements.md).
-- -----------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION app.listing_transition_allowed(p_from app.listing_status, p_to app.listing_status)
    RETURNS boolean
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT CASE
        WHEN p_from IS NULL THEN p_to = 'draft'
        WHEN p_to = 'removed' THEN p_from NOT IN ('removed', 'screening')
        ELSE (p_from::text || '>' || p_to::text) IN (
            'draft>screening',
            'screening>pending_review',
            'pending_review>verified', 'pending_review>rejected',
            'rejected>draft', 'rejected>verified',
            'verified>hidden', 'verified>expired', 'verified>screening', 'verified>draft',
            'hidden>verified',
            'expired>screening', 'expired>draft')
    END
$$;

-- -----------------------------------------------------------------------------
-- 3. Tiện nghi trùng nghĩa "khép kín" (dùng cột is_self_contained thay thế)
-- -----------------------------------------------------------------------------
UPDATE app.amenities SET is_active = false WHERE code = 'private_bathroom';
