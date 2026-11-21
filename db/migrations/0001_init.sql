-- =============================================================================
-- 0001_init.sql — Trọ & Việc Sạch: lược đồ lõi (32 bảng, 3NF)
-- Chạy bằng role app_migrator. Điều kiện trước: db/init/00_init.sh đã tạo
-- extension (postgis, vector, pg_trgm, unaccent), các role và schema app/reporting.
-- Quy ước: docs/03-database.md. KHÔNG sửa file này sau khi đã chạy ở bất kỳ môi
-- trường nào; mọi thay đổi đi vào migration mới.
-- =============================================================================

SET search_path = app, public;

-- -----------------------------------------------------------------------------
-- Hàm tiện ích
-- -----------------------------------------------------------------------------

-- unaccent() không IMMUTABLE nên không dùng được trong chỉ mục/cột sinh;
-- bọc lại với từ điển chỉ định rõ để có thể khai báo IMMUTABLE.
CREATE FUNCTION app.immutable_unaccent(text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    AS $$ SELECT public.unaccent('public.unaccent'::regdictionary, $1) $$;

-- Truy vấn full-text kiểu OR cho câu tự nhiên: plainto_tsquery mặc định AND mọi từ,
-- quá chặt khi người dùng gõ cả câu. Xếp hạng do ts_rank_cd đảm nhận.
CREATE FUNCTION app.or_tsquery(text) RETURNS tsquery
    LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
    AS $$ SELECT replace(plainto_tsquery('simple'::regconfig, app.immutable_unaccent($1))::text, ' & ', ' | ')::tsquery $$;

CREATE FUNCTION app.set_updated_at() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END
$$;

-- -----------------------------------------------------------------------------
-- Kiểu liệt kê (tập giá trị cố định; tập do quản trị viên chỉnh dùng bảng danh mục)
-- -----------------------------------------------------------------------------
CREATE TYPE app.user_status      AS ENUM ('active', 'locked', 'anonymized');
CREATE TYPE app.owner_type       AS ENUM ('individual', 'business');
CREATE TYPE app.admin_unit_level AS ENUM ('province', 'commune');
CREATE TYPE app.listing_type     AS ENUM ('room', 'job');
CREATE TYPE app.listing_status   AS ENUM ('draft', 'screening', 'pending_review',
                                          'verified', 'rejected', 'hidden', 'removed', 'expired');
CREATE TYPE app.pay_unit         AS ENUM ('hour', 'shift', 'day', 'month');
CREATE TYPE app.review_status    AS ENUM ('published', 'hidden');
CREATE TYPE app.model_component  AS ENUM ('ocr', 'llm', 'text_classifier', 'image_embedding',
                                          'text_embedding', 'aggregator');
CREATE TYPE app.scan_source      AS ENUM ('quick_check', 'listing');
CREATE TYPE app.scan_status      AS ENUM ('queued', 'processing', 'completed', 'failed');
CREATE TYPE app.risk_level       AS ENUM ('low', 'caution', 'high');
CREATE TYPE app.criterion_scope  AS ENUM ('room', 'job', 'both');
CREATE TYPE app.identifier_kind  AS ENUM ('phone', 'bank_account', 'url');
CREATE TYPE app.report_category  AS ENUM ('scam', 'misleading', 'inappropriate', 'other');
CREATE TYPE app.report_status    AS ENUM ('open', 'confirmed', 'dismissed');
CREATE TYPE app.appeal_status    AS ENUM ('open', 'accepted', 'rejected');
CREATE TYPE app.document_type    AS ENUM ('id_card', 'business_registration', 'property_proof',
                                          'authorization_letter', 'other');
CREATE TYPE app.document_status  AS ENUM ('pending', 'accepted', 'rejected');

-- =============================================================================
-- NHÓM 1 — NGƯỜI DÙNG VÀ PHÂN QUYỀN (6 bảng)
-- =============================================================================

CREATE TABLE app.users (
    id                BIGINT GENERATED ALWAYS AS IDENTITY,
    email             VARCHAR(254),             -- NULL: tài khoản đã ẩn danh hóa
    email_verified_at TIMESTAMPTZ,              -- NULL: chưa xác thực email
    password_hash     VARCHAR(255)    NOT NULL, -- chuỗi PHC Argon2id
    full_name         VARCHAR(100)    NOT NULL,
    phone_e164        VARCHAR(16),              -- NULL: chưa khai báo số điện thoại
    phone_verified_at TIMESTAMPTZ,              -- NULL: chưa xác thực OTP, chưa được đăng tin
    status            app.user_status NOT NULL DEFAULT 'active',
    anonymized_at     TIMESTAMPTZ,              -- NULL: chưa ẩn danh hóa
    created_at        TIMESTAMPTZ     NOT NULL DEFAULT now(),
    updated_at        TIMESTAMPTZ     NOT NULL DEFAULT now(),
    CONSTRAINT pk_users PRIMARY KEY (id),
    CONSTRAINT ck_users_full_name_not_blank CHECK (btrim(full_name) <> ''),
    CONSTRAINT ck_users_email_format CHECK (email IS NULL OR email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
    CONSTRAINT ck_users_email_present CHECK (status = 'anonymized' OR email IS NOT NULL),
    CONSTRAINT ck_users_email_verified_has_email CHECK (email_verified_at IS NULL OR email IS NOT NULL),
    CONSTRAINT ck_users_phone_format CHECK (phone_e164 IS NULL OR phone_e164 ~ '^\+84[0-9]{9}$'),
    CONSTRAINT ck_users_phone_verified_has_phone CHECK (phone_verified_at IS NULL OR phone_e164 IS NOT NULL),
    CONSTRAINT ck_users_anonymized CHECK ((status = 'anonymized') = (anonymized_at IS NOT NULL))
);
CREATE UNIQUE INDEX uq_users_email ON app.users (lower(email)) WHERE email IS NOT NULL;
CREATE UNIQUE INDEX uq_users_phone_e164 ON app.users (phone_e164) WHERE phone_e164 IS NOT NULL;
CREATE TRIGGER trg_users_updated_at BEFORE UPDATE ON app.users
    FOR EACH ROW EXECUTE FUNCTION app.set_updated_at();
COMMENT ON TABLE app.users IS 'Tài khoản người dùng; không xóa cứng, yêu cầu xóa dữ liệu thì ẩn danh hóa.';

CREATE TABLE app.roles (
    id   SMALLINT GENERATED ALWAYS AS IDENTITY,
    code VARCHAR(30) NOT NULL,
    name VARCHAR(60) NOT NULL,
    CONSTRAINT pk_roles PRIMARY KEY (id),
    CONSTRAINT uq_roles_code UNIQUE (code),
    CONSTRAINT ck_roles_code_format CHECK (code ~ '^[a-z][a-z_]*$')
);
COMMENT ON TABLE app.roles IS 'Vai trò: student, owner, moderator, admin.';

CREATE TABLE app.permissions (
    id          SMALLINT GENERATED ALWAYS AS IDENTITY,
    code        VARCHAR(50)  NOT NULL,
    description VARCHAR(200) NOT NULL,
    CONSTRAINT pk_permissions PRIMARY KEY (id),
    CONSTRAINT uq_permissions_code UNIQUE (code),
    CONSTRAINT ck_permissions_code_format CHECK (code ~ '^[a-z_]+\.[a-z_]+$')
);
COMMENT ON TABLE app.permissions IS 'Quyền dạng tài_nguyên.hành_động, ánh xạ 1-1 với policy của API.';

CREATE TABLE app.user_roles (
    user_id    BIGINT      NOT NULL,
    role_id    SMALLINT    NOT NULL,
    granter_id BIGINT      NOT NULL,  -- tài khoản system khi người dùng tự đăng ký
    granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT pk_user_roles PRIMARY KEY (user_id, role_id),
    CONSTRAINT fk_user_roles_user_id FOREIGN KEY (user_id) REFERENCES app.users (id),
    CONSTRAINT fk_user_roles_role_id FOREIGN KEY (role_id) REFERENCES app.roles (id),
    CONSTRAINT fk_user_roles_granter_id FOREIGN KEY (granter_id) REFERENCES app.users (id)
);
CREATE INDEX ix_user_roles_role_id ON app.user_roles (role_id);
COMMENT ON TABLE app.user_roles IS 'Bảng nối N-N người dùng – vai trò.';

CREATE TABLE app.role_permissions (
    role_id       SMALLINT NOT NULL,
    permission_id SMALLINT NOT NULL,
    CONSTRAINT pk_role_permissions PRIMARY KEY (role_id, permission_id),
    CONSTRAINT fk_role_permissions_role_id FOREIGN KEY (role_id) REFERENCES app.roles (id),
    CONSTRAINT fk_role_permissions_permission_id FOREIGN KEY (permission_id) REFERENCES app.permissions (id)
);
COMMENT ON TABLE app.role_permissions IS 'Bảng nối N-N vai trò – quyền.';

CREATE TABLE app.owner_profiles (
    user_id        BIGINT         NOT NULL,
    owner_type     app.owner_type NOT NULL,
    business_name  VARCHAR(200),   -- NULL: cá nhân, không có tên kinh doanh
    tax_code       VARCHAR(14),    -- NULL: cá nhân không khai mã số thuế
    verified_until TIMESTAMPTZ,    -- NULL: chưa được xác minh
    verifier_id    BIGINT,         -- NULL khi chưa được xác minh
    created_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ    NOT NULL DEFAULT now(),
    CONSTRAINT pk_owner_profiles PRIMARY KEY (user_id),
    CONSTRAINT fk_owner_profiles_user_id FOREIGN KEY (user_id) REFERENCES app.users (id),
    CONSTRAINT fk_owner_profiles_verifier_id FOREIGN KEY (verifier_id) REFERENCES app.users (id),
    CONSTRAINT ck_owner_profiles_tax_code CHECK (tax_code IS NULL OR tax_code ~ '^[0-9]{10}(-[0-9]{3})?$'),
    CONSTRAINT ck_owner_profiles_business_name_not_blank CHECK (business_name IS NULL OR btrim(business_name) <> ''),
    CONSTRAINT ck_owner_profiles_business_fields CHECK (owner_type = 'individual'
        OR (tax_code IS NOT NULL AND business_name IS NOT NULL)),
    CONSTRAINT ck_owner_profiles_verified CHECK ((verified_until IS NULL) = (verifier_id IS NULL))
);
CREATE TRIGGER trg_owner_profiles_updated_at BEFORE UPDATE ON app.owner_profiles
    FOR EACH ROW EXECUTE FUNCTION app.set_updated_at();
COMMENT ON TABLE app.owner_profiles IS 'Bảng con 1-1 tùy chọn của users: hồ sơ chủ trọ / nhà tuyển dụng.';

-- =============================================================================
-- DANH MỤC DÙNG CHUNG (khai báo sớm vì nhiều bảng tham chiếu)
-- =============================================================================

CREATE TABLE app.administrative_units (
    code        VARCHAR(10)          NOT NULL,  -- tỉnh 2 chữ số, xã 5 chữ số (QĐ 19/2025/QĐ-TTg)
    name        VARCHAR(100)         NOT NULL,
    unit_level  app.admin_unit_level NOT NULL,
    parent_code VARCHAR(10),                    -- NULL: cấp tỉnh
    CONSTRAINT pk_administrative_units PRIMARY KEY (code),
    CONSTRAINT fk_administrative_units_parent_code FOREIGN KEY (parent_code)
        REFERENCES app.administrative_units (code),
    CONSTRAINT ck_administrative_units_code CHECK (code ~ '^([0-9]{2}|[0-9]{5})$'),
    CONSTRAINT ck_administrative_units_parent CHECK ((unit_level = 'province') = (parent_code IS NULL)),
    CONSTRAINT ck_administrative_units_name_not_blank CHECK (btrim(name) <> '')
);
CREATE INDEX ix_administrative_units_parent_code ON app.administrative_units (parent_code);
CREATE INDEX ix_administrative_units_name_trgm ON app.administrative_units
    USING gin (app.immutable_unaccent(lower(name)) public.gin_trgm_ops);
COMMENT ON TABLE app.administrative_units IS 'Danh mục 34 tỉnh và 3.321 xã sau sắp xếp hai cấp (từ 01/7/2025).';

CREATE TABLE app.model_versions (
    id           SMALLINT GENERATED ALWAYS AS IDENTITY,
    component    app.model_component NOT NULL,
    name         VARCHAR(100)        NOT NULL,
    version      VARCHAR(50)         NOT NULL,
    is_active    BOOLEAN             NOT NULL DEFAULT false,
    activated_at TIMESTAMPTZ,        -- NULL: chưa từng được kích hoạt
    created_at   TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_model_versions PRIMARY KEY (id),
    CONSTRAINT uq_model_versions_component_name_version UNIQUE (component, name, version),
    CONSTRAINT ck_model_versions_active_has_time CHECK (NOT is_active OR activated_at IS NOT NULL)
);
CREATE UNIQUE INDEX uq_model_versions_one_active ON app.model_versions (component) WHERE is_active;
COMMENT ON TABLE app.model_versions IS 'Phiên bản mô hình; ngưỡng điểm thuộc phiên bản bộ gộp (tệp cấu hình trong Git). Mỗi thành phần chỉ một phiên bản đang chạy.';

-- =============================================================================
-- NHÓM 2 — TIN ĐĂNG (10 bảng)
-- =============================================================================

CREATE TABLE app.listings (
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    public_id      UUID                   NOT NULL DEFAULT gen_random_uuid(),
    poster_id      BIGINT                 NOT NULL,
    listing_type   app.listing_type       NOT NULL,
    title          VARCHAR(150)           NOT NULL,
    description    VARCHAR(5000)          NOT NULL,
    ward_code      VARCHAR(10)            NOT NULL,
    street_address VARCHAR(200)           NOT NULL,
    location       public.geography(Point, 4326) NOT NULL,
    status         app.listing_status     NOT NULL DEFAULT 'draft',
    expires_at     TIMESTAMPTZ,           -- NULL: tin chưa từng được duyệt
    version        INTEGER                NOT NULL DEFAULT 1,
    search_tsv     tsvector GENERATED ALWAYS AS (
                       to_tsvector('simple'::regconfig,
                                   app.immutable_unaccent(title || ' ' || description))) STORED,
    created_at     TIMESTAMPTZ            NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ            NOT NULL DEFAULT now(),
    CONSTRAINT pk_listings PRIMARY KEY (id),
    CONSTRAINT uq_listings_public_id UNIQUE (public_id),
    CONSTRAINT uq_listings_id_type UNIQUE (id, listing_type),  -- đích cho FK kép của bảng con
    CONSTRAINT fk_listings_poster_id FOREIGN KEY (poster_id) REFERENCES app.users (id),
    CONSTRAINT fk_listings_ward_code FOREIGN KEY (ward_code) REFERENCES app.administrative_units (code),
    CONSTRAINT ck_listings_title_not_blank CHECK (btrim(title) <> ''),
    CONSTRAINT ck_listings_description_length CHECK (char_length(btrim(description)) >= 30),
    CONSTRAINT ck_listings_street_address_not_blank CHECK (btrim(street_address) <> ''),
    CONSTRAINT ck_listings_verified_has_expiry CHECK (status <> 'verified' OR expires_at IS NOT NULL),
    CONSTRAINT ck_listings_version_positive CHECK (version > 0)
);
CREATE INDEX ix_listings_poster_id ON app.listings (poster_id);
CREATE INDEX ix_listings_ward_code ON app.listings (ward_code);
CREATE INDEX ix_listings_verified_created ON app.listings (created_at DESC) WHERE status = 'verified';
CREATE INDEX ix_listings_verified_expires_at ON app.listings (expires_at) WHERE status = 'verified';
CREATE INDEX ix_listings_pending ON app.listings (id) WHERE status = 'pending_review';
CREATE INDEX ix_listings_location ON app.listings USING gist (location);
CREATE INDEX ix_listings_search_tsv ON app.listings USING gin (search_tsv);
CREATE TRIGGER trg_listings_updated_at BEFORE UPDATE ON app.listings
    FOR EACH ROW EXECUTE FUNCTION app.set_updated_at();
COMMENT ON TABLE app.listings IS 'Phần chung của mọi tin đăng. Không xóa cứng: gỡ tin = chuyển sang removed.';

CREATE TABLE app.room_listings (
    listing_id        BIGINT           NOT NULL,
    listing_type      app.listing_type NOT NULL DEFAULT 'room',
    monthly_rent_vnd  INTEGER          NOT NULL,
    deposit_vnd       INTEGER,         -- NULL: chưa nêu; 0: không cần cọc
    area_m2           NUMERIC(5,1)     NOT NULL,
    is_self_contained BOOLEAN          NOT NULL,
    CONSTRAINT pk_room_listings PRIMARY KEY (listing_id),
    CONSTRAINT fk_room_listings_listing FOREIGN KEY (listing_id, listing_type)
        REFERENCES app.listings (id, listing_type) ON DELETE CASCADE,
    CONSTRAINT ck_room_listings_type CHECK (listing_type = 'room'),
    CONSTRAINT ck_room_listings_rent CHECK (monthly_rent_vnd BETWEEN 100000 AND 100000000),
    CONSTRAINT ck_room_listings_deposit CHECK (deposit_vnd >= 0),
    CONSTRAINT ck_room_listings_area CHECK (area_m2 > 0 AND area_m2 <= 500)
);
COMMENT ON TABLE app.room_listings IS 'Bảng con 1-1 cho tin phòng trọ.';

CREATE TABLE app.job_listings (
    listing_id     BIGINT           NOT NULL,
    listing_type   app.listing_type NOT NULL DEFAULT 'job',
    employer_name  VARCHAR(200)     NOT NULL,
    pay_vnd        INTEGER          NOT NULL,
    pay_unit       app.pay_unit     NOT NULL,
    hours_per_week SMALLINT         NOT NULL,
    CONSTRAINT pk_job_listings PRIMARY KEY (listing_id),
    CONSTRAINT fk_job_listings_listing FOREIGN KEY (listing_id, listing_type)
        REFERENCES app.listings (id, listing_type) ON DELETE CASCADE,
    CONSTRAINT ck_job_listings_type CHECK (listing_type = 'job'),
    CONSTRAINT ck_job_listings_employer_not_blank CHECK (btrim(employer_name) <> ''),
    CONSTRAINT ck_job_listings_pay CHECK (pay_vnd > 0 AND pay_vnd <= 100000000),
    CONSTRAINT ck_job_listings_hours CHECK (hours_per_week BETWEEN 1 AND 48)
);
COMMENT ON TABLE app.job_listings IS 'Bảng con 1-1 cho tin việc làm thêm; địa điểm làm việc là listings.location.';

CREATE TABLE app.listing_images (
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    listing_id     BIGINT        NOT NULL,
    object_key     VARCHAR(300)  NOT NULL,
    sha256         BYTEA         NOT NULL,
    width_px       SMALLINT      NOT NULL,
    height_px      SMALLINT      NOT NULL,
    sort_order     SMALLINT      NOT NULL,
    phash          BIGINT,                    -- NULL: worker chưa tính
    clip_embedding public.vector(512),        -- NULL: worker chưa tính
    clip_model_id  SMALLINT,                  -- NULL khi clip_embedding NULL
    created_at     TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT pk_listing_images PRIMARY KEY (id),
    CONSTRAINT uq_listing_images_object_key UNIQUE (object_key),
    CONSTRAINT uq_listing_images_listing_sha256 UNIQUE (listing_id, sha256),
    CONSTRAINT uq_listing_images_listing_sort UNIQUE (listing_id, sort_order) DEFERRABLE INITIALLY DEFERRED,
    CONSTRAINT fk_listing_images_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id) ON DELETE CASCADE,
    CONSTRAINT fk_listing_images_clip_model_id FOREIGN KEY (clip_model_id) REFERENCES app.model_versions (id),
    CONSTRAINT ck_listing_images_sha256_len CHECK (octet_length(sha256) = 32),
    CONSTRAINT ck_listing_images_size CHECK (width_px > 0 AND height_px > 0),
    CONSTRAINT ck_listing_images_sort CHECK (sort_order BETWEEN 1 AND 10),
    CONSTRAINT ck_listing_images_clip_pair CHECK ((clip_embedding IS NULL) = (clip_model_id IS NULL))
);
CREATE INDEX ix_listing_images_clip_embedding ON app.listing_images
    USING hnsw (clip_embedding public.vector_cosine_ops);
COMMENT ON TABLE app.listing_images IS 'Ảnh tin đăng (đã mã hóa lại, xóa EXIF); kho đối chiếu ảnh dùng lại.';

CREATE TABLE app.amenities (
    id        SMALLINT GENERATED ALWAYS AS IDENTITY,
    code      VARCHAR(40) NOT NULL,
    name      VARCHAR(80) NOT NULL,
    is_active BOOLEAN     NOT NULL DEFAULT true,
    CONSTRAINT pk_amenities PRIMARY KEY (id),
    CONSTRAINT uq_amenities_code UNIQUE (code),
    CONSTRAINT ck_amenities_code_format CHECK (code ~ '^[a-z][a-z_]*$'),
    CONSTRAINT ck_amenities_name_not_blank CHECK (btrim(name) <> '')
);
COMMENT ON TABLE app.amenities IS 'Danh mục tiện nghi phòng.';

CREATE TABLE app.room_amenities (
    listing_id BIGINT   NOT NULL,
    amenity_id SMALLINT NOT NULL,
    CONSTRAINT pk_room_amenities PRIMARY KEY (listing_id, amenity_id),
    CONSTRAINT fk_room_amenities_listing_id FOREIGN KEY (listing_id)
        REFERENCES app.room_listings (listing_id) ON DELETE CASCADE,
    CONSTRAINT fk_room_amenities_amenity_id FOREIGN KEY (amenity_id) REFERENCES app.amenities (id)
);
CREATE INDEX ix_room_amenities_amenity_id ON app.room_amenities (amenity_id);
COMMENT ON TABLE app.room_amenities IS 'Bảng nối N-N tin phòng – tiện nghi.';

CREATE TABLE app.saved_listings (
    user_id    BIGINT      NOT NULL,
    listing_id BIGINT      NOT NULL,
    saved_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT pk_saved_listings PRIMARY KEY (user_id, listing_id),
    CONSTRAINT fk_saved_listings_user_id FOREIGN KEY (user_id) REFERENCES app.users (id),
    CONSTRAINT fk_saved_listings_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id)
);
COMMENT ON TABLE app.saved_listings IS 'Bảng nối N-N người dùng – tin đã lưu.';

CREATE TABLE app.reviews (
    id            BIGINT GENERATED ALWAYS AS IDENTITY,
    listing_id    BIGINT            NOT NULL,
    reviewer_id   BIGINT            NOT NULL,
    rating        SMALLINT          NOT NULL,
    body          VARCHAR(1000),    -- NULL: chỉ chấm sao, không viết nhận xét
    status        app.review_status NOT NULL DEFAULT 'published',
    hider_id      BIGINT,           -- NULL: đánh giá chưa bị ẩn
    hidden_reason VARCHAR(300),     -- NULL: đánh giá chưa bị ẩn
    created_at    TIMESTAMPTZ       NOT NULL DEFAULT now(),
    CONSTRAINT pk_reviews PRIMARY KEY (id),
    CONSTRAINT uq_reviews_listing_reviewer UNIQUE (listing_id, reviewer_id),
    CONSTRAINT fk_reviews_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    CONSTRAINT fk_reviews_reviewer_id FOREIGN KEY (reviewer_id) REFERENCES app.users (id),
    CONSTRAINT fk_reviews_hider_id FOREIGN KEY (hider_id) REFERENCES app.users (id),
    CONSTRAINT ck_reviews_rating CHECK (rating BETWEEN 1 AND 5),
    CONSTRAINT ck_reviews_body_not_blank CHECK (body IS NULL OR btrim(body) <> ''),
    CONSTRAINT ck_reviews_hidden CHECK (
        (status = 'published' AND hider_id IS NULL AND hidden_reason IS NULL)
     OR (status = 'hidden' AND hider_id IS NOT NULL AND hidden_reason IS NOT NULL AND btrim(hidden_reason) <> ''))
);
CREATE INDEX ix_reviews_reviewer_id ON app.reviews (reviewer_id);
COMMENT ON TABLE app.reviews IS 'Đánh giá của sinh viên sau khi thuê phòng hoặc làm việc; mỗi người một đánh giá cho một tin.';

-- Các cặp chuyển trạng thái hợp lệ của tin (xem docs/01-requirements.md, mục Vòng đời tin).
CREATE FUNCTION app.listing_transition_allowed(p_from app.listing_status, p_to app.listing_status)
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
            'expired>screening')
    END
$$;

CREATE TABLE app.listing_transitions (
    id          BIGINT GENERATED ALWAYS AS IDENTITY,
    listing_id  BIGINT             NOT NULL,
    from_status app.listing_status,           -- NULL: bản ghi đầu tiên khi tạo tin
    to_status   app.listing_status NOT NULL,
    actor_id    BIGINT             NOT NULL,  -- tài khoản system cho thao tác tự động
    reason      VARCHAR(500),                 -- bắt buộc khi từ chối, ẩn, gỡ
    created_at  TIMESTAMPTZ        NOT NULL DEFAULT now(),
    CONSTRAINT pk_listing_transitions PRIMARY KEY (id),
    CONSTRAINT uq_listing_transitions_id_listing UNIQUE (id, listing_id),  -- đích cho FK kép của appeals
    CONSTRAINT fk_listing_transitions_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    CONSTRAINT fk_listing_transitions_actor_id FOREIGN KEY (actor_id) REFERENCES app.users (id),
    CONSTRAINT ck_listing_transitions_allowed CHECK (app.listing_transition_allowed(from_status, to_status)),
    CONSTRAINT ck_listing_transitions_reason CHECK (
        to_status NOT IN ('rejected', 'hidden', 'removed')
        OR (reason IS NOT NULL AND btrim(reason) <> ''))
);
CREATE INDEX ix_listing_transitions_listing_created ON app.listing_transitions (listing_id, created_at DESC);
CREATE INDEX ix_listing_transitions_actor_created ON app.listing_transitions (actor_id, created_at DESC);
COMMENT ON TABLE app.listing_transitions IS 'Lịch sử trạng thái của tin, chỉ thêm (audit log kiểm duyệt).';

-- =============================================================================
-- NHÓM 3 — SÀNG LỌC AI (12 bảng, tính cả model_versions khai báo ở trên)
-- =============================================================================

CREATE TABLE app.listing_embeddings (
    listing_id       BIGINT              NOT NULL,
    embedding        public.vector(1024) NOT NULL,
    model_version_id SMALLINT            NOT NULL,
    updated_at       TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_listing_embeddings PRIMARY KEY (listing_id),
    CONSTRAINT fk_listing_embeddings_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id) ON DELETE CASCADE,
    CONSTRAINT fk_listing_embeddings_model_version_id FOREIGN KEY (model_version_id) REFERENCES app.model_versions (id)
);
CREATE INDEX ix_listing_embeddings_embedding ON app.listing_embeddings
    USING hnsw (embedding public.vector_cosine_ops);
COMMENT ON TABLE app.listing_embeddings IS 'Embedding BGE-M3 của tin, tách khỏi listings để bảng nóng gọn nhẹ.';

CREATE TABLE app.scans (
    id               BIGINT GENERATED ALWAYS AS IDENTITY,
    public_id        UUID            NOT NULL DEFAULT gen_random_uuid(),
    source           app.scan_source NOT NULL,
    listing_id       BIGINT,          -- NULL: lượt kiểm tra nhanh, không gắn tin
    requester_id     BIGINT,          -- NULL: khách chưa đăng nhập
    status           app.scan_status NOT NULL DEFAULT 'queued',
    input_text       VARCHAR(10000),  -- NULL: người dùng chỉ gửi ảnh
    claimed_rent_vnd INTEGER,         -- NULL: người dùng không nêu giá thuê
    claimed_ward_code VARCHAR(10),    -- NULL: người dùng không nêu khu vực
    input_sha256     BYTEA           NOT NULL,  -- băm nội dung chuẩn hóa, dùng cho cache
    error_code       VARCHAR(50),     -- NULL trừ khi status = failed
    created_at       TIMESTAMPTZ     NOT NULL DEFAULT now(),
    completed_at     TIMESTAMPTZ,     -- NULL: chưa xử lý xong
    expires_at       TIMESTAMPTZ,     -- NULL: lượt sàng lọc tin đăng, sống cùng tin
    CONSTRAINT pk_scans PRIMARY KEY (id),
    CONSTRAINT uq_scans_public_id UNIQUE (public_id),
    CONSTRAINT fk_scans_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    CONSTRAINT fk_scans_requester_id FOREIGN KEY (requester_id) REFERENCES app.users (id),
    CONSTRAINT fk_scans_claimed_ward_code FOREIGN KEY (claimed_ward_code) REFERENCES app.administrative_units (code),
    CONSTRAINT ck_scans_source_consistency CHECK (
        (source = 'quick_check' AND listing_id IS NULL AND expires_at IS NOT NULL)
     OR (source = 'listing' AND listing_id IS NOT NULL AND expires_at IS NULL
         AND input_text IS NOT NULL AND claimed_rent_vnd IS NULL AND claimed_ward_code IS NULL)),
    CONSTRAINT ck_scans_input_text_not_blank CHECK (input_text IS NULL OR btrim(input_text) <> ''),
    CONSTRAINT ck_scans_claimed_rent CHECK (claimed_rent_vnd IS NULL OR claimed_rent_vnd BETWEEN 100000 AND 100000000),
    CONSTRAINT ck_scans_sha256_len CHECK (octet_length(input_sha256) = 32),
    CONSTRAINT ck_scans_failed_has_error CHECK ((status = 'failed') = (error_code IS NOT NULL)),
    CONSTRAINT ck_scans_completed_at CHECK ((status IN ('completed', 'failed')) = (completed_at IS NOT NULL))
);
CREATE INDEX ix_scans_listing_id_created ON app.scans (listing_id, created_at DESC) WHERE listing_id IS NOT NULL;
CREATE INDEX ix_scans_requester_id_created ON app.scans (requester_id, created_at DESC) WHERE requester_id IS NOT NULL;
CREATE INDEX ix_scans_expires_at ON app.scans (expires_at) WHERE expires_at IS NOT NULL;
CREATE INDEX ix_scans_input_sha256 ON app.scans (input_sha256);
COMMENT ON TABLE app.scans IS 'Mỗi lượt sàng lọc: kiểm tra nhanh của người dùng hoặc sàng lọc tin trước khi duyệt.';

CREATE TABLE app.scan_images (
    id         BIGINT GENERATED ALWAYS AS IDENTITY,
    scan_id    BIGINT        NOT NULL,
    image_no   SMALLINT      NOT NULL,
    object_key VARCHAR(300)  NOT NULL,
    sha256     BYTEA         NOT NULL,
    ocr_text   VARCHAR(10000),  -- NULL: ảnh không có chữ đọc được (chỉ có nghĩa khi scan đã completed)
    CONSTRAINT pk_scan_images PRIMARY KEY (id),
    CONSTRAINT uq_scan_images_scan_image_no UNIQUE (scan_id, image_no),
    CONSTRAINT uq_scan_images_object_key UNIQUE (object_key),
    CONSTRAINT fk_scan_images_scan_id FOREIGN KEY (scan_id) REFERENCES app.scans (id) ON DELETE CASCADE,
    CONSTRAINT ck_scan_images_image_no CHECK (image_no BETWEEN 1 AND 5),
    CONSTRAINT ck_scan_images_sha256_len CHECK (octet_length(sha256) = 32),
    CONSTRAINT ck_scan_images_ocr_not_blank CHECK (ocr_text IS NULL OR btrim(ocr_text) <> '')
);
COMMENT ON TABLE app.scan_images IS 'Ảnh người dùng gửi khi kiểm tra nhanh; không đưa vào kho đối chiếu, xóa cùng lượt kiểm tra.';

CREATE TABLE app.criteria (
    id          SMALLINT GENERATED ALWAYS AS IDENTITY,
    code        VARCHAR(50)         NOT NULL,
    name        VARCHAR(150)        NOT NULL,
    guidance    VARCHAR(1000)       NOT NULL,  -- mô tả cho LLM và cán bộ
    scope       app.criterion_scope NOT NULL,
    legal_basis VARCHAR(300),                  -- NULL: không có căn cứ pháp lý cụ thể
    is_active   BOOLEAN             NOT NULL DEFAULT true,
    created_at  TIMESTAMPTZ         NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_criteria PRIMARY KEY (id),
    CONSTRAINT uq_criteria_code UNIQUE (code),
    CONSTRAINT ck_criteria_code_format CHECK (code ~ '^[a-z][a-z_]*$'),
    CONSTRAINT ck_criteria_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT ck_criteria_guidance_not_blank CHECK (btrim(guidance) <> ''),
    CONSTRAINT ck_criteria_legal_basis_not_blank CHECK (legal_basis IS NULL OR btrim(legal_basis) <> '')
);
CREATE TRIGGER trg_criteria_updated_at BEFORE UPDATE ON app.criteria
    FOR EACH ROW EXECUTE FUNCTION app.set_updated_at();
COMMENT ON TABLE app.criteria IS 'Bộ tiêu chí dấu hiệu lừa đảo; ngừng dùng bằng is_active thay vì xóa.';

CREATE TABLE app.assessments (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY,
    scan_id             BIGINT         NOT NULL,
    aggregator_model_id SMALLINT       NOT NULL,
    llm_model_id        SMALLINT,      -- NULL: lượt chấm không gọi LLM
    score               SMALLINT       NOT NULL,
    risk_level          app.risk_level NOT NULL,  -- ảnh chụp quyết định đã hiển thị, không cập nhật lại
    text_signal         REAL,          -- 0..1; NULL: không có chữ để chấm
    image_signal        REAL,          -- 0..1; NULL: không có ảnh
    price_signal        REAL,          -- 0..1; NULL: không có giá hoặc chưa đủ 10 phòng để so
    community_signal    REAL,          -- 0..1; NULL: không trích được định danh nào
    price_median_vnd    INTEGER,       -- NULL khi price_signal NULL
    price_sample_size   SMALLINT,      -- NULL khi price_signal NULL
    created_at          TIMESTAMPTZ    NOT NULL DEFAULT now(),
    CONSTRAINT pk_assessments PRIMARY KEY (id),
    CONSTRAINT uq_assessments_scan_aggregator UNIQUE (scan_id, aggregator_model_id),  -- chống ghi trùng khi việc bị giao lại
    CONSTRAINT fk_assessments_scan_id FOREIGN KEY (scan_id) REFERENCES app.scans (id) ON DELETE CASCADE,
    CONSTRAINT fk_assessments_aggregator_model_id FOREIGN KEY (aggregator_model_id) REFERENCES app.model_versions (id),
    CONSTRAINT fk_assessments_llm_model_id FOREIGN KEY (llm_model_id) REFERENCES app.model_versions (id),
    CONSTRAINT ck_assessments_score CHECK (score BETWEEN 0 AND 100),
    CONSTRAINT ck_assessments_signals CHECK (
            (text_signal      IS NULL OR text_signal      BETWEEN 0 AND 1)
        AND (image_signal     IS NULL OR image_signal     BETWEEN 0 AND 1)
        AND (price_signal     IS NULL OR price_signal     BETWEEN 0 AND 1)
        AND (community_signal IS NULL OR community_signal BETWEEN 0 AND 1)),
    CONSTRAINT ck_assessments_price_context CHECK (
            (price_signal IS NULL) = (price_median_vnd IS NULL)
        AND (price_signal IS NULL) = (price_sample_size IS NULL)),
    CONSTRAINT ck_assessments_price_sample CHECK (price_sample_size IS NULL OR price_sample_size >= 10)
);
COMMENT ON TABLE app.assessments IS 'Kết quả chấm rủi ro, chỉ thêm. Thời gian xử lý = created_at - scans.created_at.';

CREATE TABLE app.assessment_flags (
    assessment_id BIGINT       NOT NULL,
    criterion_id  SMALLINT     NOT NULL,
    evidence_text VARCHAR(500) NOT NULL,  -- câu trích nguyên văn (đã che PII) làm bằng chứng
    CONSTRAINT pk_assessment_flags PRIMARY KEY (assessment_id, criterion_id),
    CONSTRAINT fk_assessment_flags_assessment_id FOREIGN KEY (assessment_id)
        REFERENCES app.assessments (id) ON DELETE CASCADE,
    CONSTRAINT fk_assessment_flags_criterion_id FOREIGN KEY (criterion_id) REFERENCES app.criteria (id),
    CONSTRAINT ck_assessment_flags_evidence_not_blank CHECK (btrim(evidence_text) <> '')
);
CREATE INDEX ix_assessment_flags_criterion_id ON app.assessment_flags (criterion_id);
COMMENT ON TABLE app.assessment_flags IS 'Bảng nối N-N có dữ liệu: dấu hiệu rủi ro kèm bằng chứng; chỉ thêm.';

CREATE TABLE app.assessment_image_matches (
    assessment_id    BIGINT   NOT NULL,
    listing_image_id BIGINT   NOT NULL,
    source_image_no  SMALLINT NOT NULL,  -- ảnh thứ mấy trong đầu vào của lượt sàng lọc
    hamming_distance SMALLINT NOT NULL,
    clip_similarity  REAL     NOT NULL,
    CONSTRAINT pk_assessment_image_matches PRIMARY KEY (assessment_id, listing_image_id),
    CONSTRAINT fk_assessment_image_matches_assessment_id FOREIGN KEY (assessment_id)
        REFERENCES app.assessments (id) ON DELETE CASCADE,
    CONSTRAINT fk_assessment_image_matches_listing_image_id FOREIGN KEY (listing_image_id)
        REFERENCES app.listing_images (id) ON DELETE CASCADE,
    CONSTRAINT ck_assessment_image_matches_source_no CHECK (source_image_no BETWEEN 1 AND 10),
    CONSTRAINT ck_assessment_image_matches_hamming CHECK (hamming_distance BETWEEN 0 AND 64),
    CONSTRAINT ck_assessment_image_matches_clip CHECK (clip_similarity BETWEEN -1 AND 1)
);
CREATE INDEX ix_assessment_image_matches_listing_image_id ON app.assessment_image_matches (listing_image_id);
COMMENT ON TABLE app.assessment_image_matches IS 'Bằng chứng ảnh dùng lại: ảnh đầu vào khớp với ảnh của tin khác.';

CREATE TABLE app.identifiers (
    id         BIGINT GENERATED ALWAYS AS IDENTITY,
    kind       app.identifier_kind NOT NULL,
    value_hmac BYTEA               NOT NULL,  -- HMAC-SHA256(khóa bí mật, giá trị chuẩn hóa)
    hint       VARCHAR(40)         NOT NULL,  -- vd "•••789" hoặc tên miền; không khôi phục được giá trị
    created_at TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_identifiers PRIMARY KEY (id),
    CONSTRAINT uq_identifiers_kind_value UNIQUE (kind, value_hmac),
    CONSTRAINT ck_identifiers_hmac_len CHECK (octet_length(value_hmac) = 32),
    CONSTRAINT ck_identifiers_hint_not_blank CHECK (btrim(hint) <> '')
);
COMMENT ON TABLE app.identifiers IS 'Số điện thoại, số tài khoản, đường link của bên thứ ba, chỉ lưu dạng HMAC.';

CREATE TABLE app.scan_identifiers (
    scan_id       BIGINT NOT NULL,
    identifier_id BIGINT NOT NULL,
    CONSTRAINT pk_scan_identifiers PRIMARY KEY (scan_id, identifier_id),
    CONSTRAINT fk_scan_identifiers_scan_id FOREIGN KEY (scan_id) REFERENCES app.scans (id) ON DELETE CASCADE,
    CONSTRAINT fk_scan_identifiers_identifier_id FOREIGN KEY (identifier_id) REFERENCES app.identifiers (id)
);
CREATE INDEX ix_scan_identifiers_identifier_id ON app.scan_identifiers (identifier_id);
COMMENT ON TABLE app.scan_identifiers IS 'Bảng nối N-N: định danh trích ra từ mỗi lượt sàng lọc.';

CREATE TABLE app.identifier_stats (
    identifier_id          BIGINT      NOT NULL,
    confirmed_report_count INTEGER     NOT NULL DEFAULT 0,
    last_confirmed_at      TIMESTAMPTZ,  -- NULL: chưa có báo cáo nào được xác nhận
    CONSTRAINT pk_identifier_stats PRIMARY KEY (identifier_id),
    CONSTRAINT fk_identifier_stats_identifier_id FOREIGN KEY (identifier_id) REFERENCES app.identifiers (id),
    CONSTRAINT ck_identifier_stats_count CHECK (confirmed_report_count >= 0),
    CONSTRAINT ck_identifier_stats_last CHECK ((confirmed_report_count = 0) = (last_confirmed_at IS NULL))
);
COMMENT ON TABLE app.identifier_stats IS 'Khử chuẩn hóa có chủ đích: bộ đếm lịch sử báo cáo đã xác nhận, chỉ trigger được ghi.';

CREATE TABLE app.outbox_messages (
    id           BIGINT GENERATED ALWAYS AS IDENTITY,
    scan_id      BIGINT,       -- việc chấm điểm một lượt sàng lọc
    listing_id   BIGINT,       -- việc tính lại embedding của tin
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    published_at TIMESTAMPTZ,  -- NULL: chưa đẩy sang Redis
    CONSTRAINT pk_outbox_messages PRIMARY KEY (id),
    CONSTRAINT fk_outbox_messages_scan_id FOREIGN KEY (scan_id) REFERENCES app.scans (id) ON DELETE CASCADE,
    CONSTRAINT fk_outbox_messages_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    CONSTRAINT ck_outbox_messages_one_target CHECK (num_nonnulls(scan_id, listing_id) = 1)
);
CREATE INDEX ix_outbox_messages_unpublished ON app.outbox_messages (id) WHERE published_at IS NULL;
CREATE INDEX ix_outbox_messages_scan_id ON app.outbox_messages (scan_id) WHERE scan_id IS NOT NULL;
COMMENT ON TABLE app.outbox_messages IS 'Transactional outbox: việc chờ đẩy sang Redis Streams, ghi cùng transaction với dữ liệu nghiệp vụ.';

-- =============================================================================
-- NHÓM 4 — KIỂM DUYỆT (4 bảng)
-- =============================================================================

CREATE TABLE app.reports (
    id              BIGINT GENERATED ALWAYS AS IDENTITY,
    reporter_id     BIGINT              NOT NULL,
    listing_id      BIGINT,             -- đúng một trong listing_id, scan_id
    scan_id         BIGINT,
    category        app.report_category NOT NULL,
    description     VARCHAR(1000),      -- NULL: người báo cáo không ghi thêm
    status          app.report_status   NOT NULL DEFAULT 'open',
    resolver_id     BIGINT,             -- NULL: chưa xử lý
    resolved_at     TIMESTAMPTZ,        -- NULL: chưa xử lý
    resolution_note VARCHAR(500),       -- NULL: chưa xử lý
    created_at      TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_reports PRIMARY KEY (id),
    CONSTRAINT fk_reports_reporter_id FOREIGN KEY (reporter_id) REFERENCES app.users (id),
    CONSTRAINT fk_reports_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    CONSTRAINT fk_reports_scan_id FOREIGN KEY (scan_id) REFERENCES app.scans (id) ON DELETE CASCADE,
    CONSTRAINT fk_reports_resolver_id FOREIGN KEY (resolver_id) REFERENCES app.users (id),
    CONSTRAINT ck_reports_one_target CHECK (num_nonnulls(listing_id, scan_id) = 1),
    CONSTRAINT ck_reports_description_not_blank CHECK (description IS NULL OR btrim(description) <> ''),
    CONSTRAINT ck_reports_resolution CHECK (
        (status = 'open' AND resolver_id IS NULL AND resolved_at IS NULL AND resolution_note IS NULL)
     OR (status <> 'open' AND resolver_id IS NOT NULL AND resolved_at IS NOT NULL
         AND resolution_note IS NOT NULL AND btrim(resolution_note) <> ''))
);
CREATE INDEX ix_reports_open_created ON app.reports (created_at) WHERE status = 'open';
CREATE INDEX ix_reports_listing_id ON app.reports (listing_id) WHERE listing_id IS NOT NULL;
CREATE INDEX ix_reports_scan_id ON app.reports (scan_id) WHERE scan_id IS NOT NULL;
CREATE UNIQUE INDEX uq_reports_open_listing_reporter ON app.reports (reporter_id, listing_id)
    WHERE status = 'open' AND listing_id IS NOT NULL;
CREATE UNIQUE INDEX uq_reports_open_scan_reporter ON app.reports (reporter_id, scan_id)
    WHERE status = 'open' AND scan_id IS NOT NULL;
COMMENT ON TABLE app.reports IS 'Báo cáo tin hoặc lượt kiểm tra đáng ngờ; xác nhận báo cáo cập nhật identifier_stats qua trigger.';

CREATE TABLE app.appeals (
    id                    BIGINT GENERATED ALWAYS AS IDENTITY,
    listing_id            BIGINT            NOT NULL,
    listing_transition_id BIGINT            NOT NULL,  -- quyết định bị kháng nghị (rejected hoặc hidden)
    reason                VARCHAR(1000)     NOT NULL,
    status                app.appeal_status NOT NULL DEFAULT 'open',
    decider_id            BIGINT,           -- NULL: chưa xử lý
    decided_at            TIMESTAMPTZ,      -- NULL: chưa xử lý
    decision_note         VARCHAR(500),     -- NULL: chưa xử lý
    created_at            TIMESTAMPTZ       NOT NULL DEFAULT now(),
    CONSTRAINT pk_appeals PRIMARY KEY (id),
    CONSTRAINT uq_appeals_listing_transition_id UNIQUE (listing_transition_id),
    CONSTRAINT fk_appeals_listing_id FOREIGN KEY (listing_id) REFERENCES app.listings (id),
    -- FK kép: listing_id luôn khớp với tin của quyết định bị kháng nghị
    CONSTRAINT fk_appeals_transition FOREIGN KEY (listing_transition_id, listing_id)
        REFERENCES app.listing_transitions (id, listing_id),
    CONSTRAINT fk_appeals_decider_id FOREIGN KEY (decider_id) REFERENCES app.users (id),
    CONSTRAINT ck_appeals_reason_not_blank CHECK (btrim(reason) <> ''),
    CONSTRAINT ck_appeals_decision CHECK (
        (status = 'open' AND decider_id IS NULL AND decided_at IS NULL AND decision_note IS NULL)
     OR (status <> 'open' AND decider_id IS NOT NULL AND decided_at IS NOT NULL
         AND decision_note IS NOT NULL AND btrim(decision_note) <> ''))
);
CREATE UNIQUE INDEX uq_appeals_one_open ON app.appeals (listing_id) WHERE status = 'open';
COMMENT ON TABLE app.appeals IS 'Kháng nghị quyết định từ chối hoặc ẩn tin; người kháng nghị suy ra qua listings.poster_id.';

CREATE TABLE app.owner_documents (
    id          BIGINT GENERATED ALWAYS AS IDENTITY,
    owner_id    BIGINT              NOT NULL,
    doc_type    app.document_type   NOT NULL,
    object_key  VARCHAR(300),       -- NULL: tệp đã bị xóa theo yêu cầu
    sha256      BYTEA               NOT NULL,  -- băm bản rõ, phát hiện một giấy tờ dùng cho nhiều tài khoản
    mime_type   VARCHAR(50)         NOT NULL,
    byte_size   INTEGER             NOT NULL,
    key_id      VARCHAR(20)         NOT NULL,  -- khóa AES đã mã hóa tệp (phục vụ xoay khóa)
    status      app.document_status NOT NULL DEFAULT 'pending',
    reviewer_id BIGINT,             -- NULL: chưa được duyệt
    reviewed_at TIMESTAMPTZ,        -- NULL: chưa được duyệt
    consent_at  TIMESTAMPTZ         NOT NULL,
    erased_at   TIMESTAMPTZ,        -- NULL: tệp còn lưu
    created_at  TIMESTAMPTZ         NOT NULL DEFAULT now(),
    CONSTRAINT pk_owner_documents PRIMARY KEY (id),
    CONSTRAINT uq_owner_documents_object_key UNIQUE (object_key),
    CONSTRAINT fk_owner_documents_owner_id FOREIGN KEY (owner_id) REFERENCES app.owner_profiles (user_id),
    CONSTRAINT fk_owner_documents_reviewer_id FOREIGN KEY (reviewer_id) REFERENCES app.users (id),
    CONSTRAINT ck_owner_documents_sha256_len CHECK (octet_length(sha256) = 32),
    CONSTRAINT ck_owner_documents_mime CHECK (mime_type IN ('image/jpeg', 'image/png', 'image/webp', 'application/pdf')),
    CONSTRAINT ck_owner_documents_size CHECK (byte_size BETWEEN 1 AND 10485760),
    CONSTRAINT ck_owner_documents_erased CHECK ((object_key IS NULL) = (erased_at IS NOT NULL)),
    CONSTRAINT ck_owner_documents_review CHECK (
        (status = 'pending' AND reviewer_id IS NULL AND reviewed_at IS NULL)
     OR (status <> 'pending' AND reviewer_id IS NOT NULL AND reviewed_at IS NOT NULL))
);
CREATE UNIQUE INDEX uq_owner_documents_sha256 ON app.owner_documents (sha256) WHERE erased_at IS NULL;
CREATE INDEX ix_owner_documents_owner_id ON app.owner_documents (owner_id);
CREATE INDEX ix_owner_documents_pending ON app.owner_documents (created_at) WHERE status = 'pending';
COMMENT ON TABLE app.owner_documents IS 'Giấy tờ xác minh, mã hóa AES-256-GCM ở tầng ứng dụng trước khi lưu vào object storage.';

CREATE TABLE app.document_access_logs (
    id                BIGINT GENERATED ALWAYS AS IDENTITY,
    owner_document_id BIGINT       NOT NULL,
    viewer_id         BIGINT       NOT NULL,
    purpose           VARCHAR(200) NOT NULL,
    accessed_at       TIMESTAMPTZ  NOT NULL DEFAULT now(),
    CONSTRAINT pk_document_access_logs PRIMARY KEY (id),
    CONSTRAINT fk_document_access_logs_owner_document_id FOREIGN KEY (owner_document_id)
        REFERENCES app.owner_documents (id),
    CONSTRAINT fk_document_access_logs_viewer_id FOREIGN KEY (viewer_id) REFERENCES app.users (id),
    CONSTRAINT ck_document_access_logs_purpose_not_blank CHECK (btrim(purpose) <> '')
);
CREATE INDEX ix_document_access_logs_document_accessed ON app.document_access_logs (owner_document_id, accessed_at DESC);
CREATE INDEX ix_document_access_logs_viewer_accessed ON app.document_access_logs (viewer_id, accessed_at DESC);
COMMENT ON TABLE app.document_access_logs IS 'Nhật ký mọi lần mở giấy tờ: ai, lúc nào, mục đích gì; chỉ thêm.';

-- =============================================================================
-- QUY TẮC VƯỢT BẢNG (CHECK không tham chiếu được bảng khác nên dùng trigger)
-- Lỗi nghiệp vụ dùng SQLSTATE P0001, MESSAGE = mã lỗi mà API trả cho client.
-- =============================================================================

-- Tài khoản system dùng làm actor cho thao tác tự động; ai cũng gọi được mà không cần quyền đọc users.
CREATE FUNCTION app.system_user_id() RETURNS bigint
    LANGUAGE sql STABLE SECURITY DEFINER
    SET search_path = app, pg_temp
    AS $$ SELECT id FROM app.users WHERE email = 'system@troviec.invalid' $$;

-- Xác nhận báo cáo -> cộng bộ đếm cho mọi định danh của lượt sàng lọc (hoặc của mọi lượt sàng lọc của tin).
-- SECURITY DEFINER để app_api không cần quyền ghi trực tiếp vào identifier_stats.
CREATE FUNCTION app.on_report_confirmed() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path = app, pg_temp
    AS $$
BEGIN
    INSERT INTO app.identifier_stats AS st (identifier_id, confirmed_report_count, last_confirmed_at)
    SELECT DISTINCT si.identifier_id, 1, NEW.resolved_at
    FROM app.scan_identifiers si
    JOIN app.scans sc ON sc.id = si.scan_id
    WHERE (NEW.scan_id IS NOT NULL AND sc.id = NEW.scan_id)
       OR (NEW.listing_id IS NOT NULL AND sc.listing_id = NEW.listing_id)
    ON CONFLICT (identifier_id) DO UPDATE
        SET confirmed_report_count = st.confirmed_report_count + 1,
            last_confirmed_at = GREATEST(st.last_confirmed_at, EXCLUDED.last_confirmed_at);
    RETURN NULL;
END
$$;
CREATE TRIGGER trg_reports_confirmed AFTER UPDATE OF status ON app.reports
    FOR EACH ROW WHEN (NEW.status = 'confirmed' AND OLD.status <> 'confirmed')
    EXECUTE FUNCTION app.on_report_confirmed();

-- Kháng nghị: chỉ nhắm vào quyết định từ chối/ẩn đang có hiệu lực, trong 7 ngày;
-- người xử lý phải khác người đã ra quyết định.
CREATE FUNCTION app.check_appeal() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    t app.listing_transitions%ROWTYPE;
    current_status app.listing_status;
BEGIN
    SELECT * INTO t FROM app.listing_transitions WHERE id = NEW.listing_transition_id;
    IF TG_OP = 'INSERT' THEN
        IF t.to_status NOT IN ('rejected', 'hidden') THEN
            RAISE EXCEPTION 'appeal.target_invalid';
        END IF;
        SELECT status INTO current_status FROM app.listings WHERE id = NEW.listing_id;
        IF current_status <> t.to_status OR EXISTS (
               SELECT 1 FROM app.listing_transitions x
               WHERE x.listing_id = NEW.listing_id AND x.id > t.id) THEN
            RAISE EXCEPTION 'appeal.decision_superseded';
        END IF;
        IF now() > t.created_at + interval '7 days' THEN
            RAISE EXCEPTION 'appeal.window_closed';
        END IF;
    ELSIF OLD.status = 'open' AND NEW.status <> 'open' AND NEW.decider_id = t.actor_id THEN
        RAISE EXCEPTION 'appeal.decider_conflict';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trg_appeals_check BEFORE INSERT OR UPDATE OF status ON app.appeals
    FOR EACH ROW EXECUTE FUNCTION app.check_appeal();

-- Chỉ tài khoản có vai trò student mới viết đánh giá, và không tự đánh giá tin của mình.
CREATE FUNCTION app.check_review() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM app.user_roles ur JOIN app.roles r ON r.id = ur.role_id
                   WHERE ur.user_id = NEW.reviewer_id AND r.code = 'student') THEN
        RAISE EXCEPTION 'review.not_student';
    END IF;
    IF EXISTS (SELECT 1 FROM app.listings l WHERE l.id = NEW.listing_id AND l.poster_id = NEW.reviewer_id) THEN
        RAISE EXCEPTION 'review.own_listing';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trg_reviews_check BEFORE INSERT ON app.reviews
    FOR EACH ROW EXECUTE FUNCTION app.check_review();

-- =============================================================================
-- OLAP NHẸ — materialized view trong schema reporting, làm mới mỗi đêm
-- (chỉ phản ánh dữ liệu còn lưu: lượt kiểm tra nhanh bị xóa sau 30 ngày)
-- =============================================================================

CREATE MATERIALIZED VIEW reporting.daily_scans AS
SELECT (s.created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day,
       s.source,
       count(*)                                         AS scan_count,
       count(*) FILTER (WHERE a.risk_level = 'high')    AS high_count,
       count(*) FILTER (WHERE a.risk_level = 'caution') AS caution_count,
       count(*) FILTER (WHERE a.risk_level = 'low')     AS low_count,
       count(*) FILTER (WHERE s.status = 'failed')      AS failed_count,
       percentile_cont(0.95) WITHIN GROUP (
           ORDER BY extract(epoch FROM a.created_at - s.created_at)) AS p95_seconds
FROM app.scans s
LEFT JOIN LATERAL (
    SELECT a1.risk_level, a1.created_at
    FROM app.assessments a1
    WHERE a1.scan_id = s.id
    ORDER BY a1.id
    LIMIT 1) a ON true
GROUP BY 1, 2;
CREATE UNIQUE INDEX uq_daily_scans ON reporting.daily_scans (day, source);

CREATE MATERIALIZED VIEW reporting.daily_moderation AS
SELECT (d.created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS day,
       count(*) AS decision_count,
       percentile_cont(0.5) WITHIN GROUP (
           ORDER BY extract(epoch FROM d.created_at - q.queued_at) / 60) AS median_minutes
FROM app.listing_transitions d
JOIN LATERAL (
    SELECT q1.created_at AS queued_at
    FROM app.listing_transitions q1
    WHERE q1.listing_id = d.listing_id
      AND q1.to_status = 'pending_review'
      AND q1.id < d.id
    ORDER BY q1.id DESC
    LIMIT 1) q ON true
WHERE d.from_status = 'pending_review' AND d.to_status IN ('verified', 'rejected')
GROUP BY 1;
CREATE UNIQUE INDEX uq_daily_moderation ON reporting.daily_moderation (day);

CREATE MATERIALIZED VIEW reporting.risk_distribution AS
SELECT s.source,
       (a.score / 10) * 10 AS score_bucket,
       count(*)            AS assessment_count
FROM app.assessments a
JOIN app.scans s ON s.id = a.scan_id
GROUP BY 1, 2;
CREATE UNIQUE INDEX uq_risk_distribution ON reporting.risk_distribution (source, score_bucket);

-- =============================================================================
-- PHÂN QUYỀN THEO ĐẶC QUYỀN TỐI THIỂU (role tạo ở db/init/00_init.sh)
-- Quy tắc: mỗi bảng mới trong migration sau phải GRANT tường minh ngay trong migration đó.
-- =============================================================================

REVOKE EXECUTE ON FUNCTION app.system_user_id() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION app.on_report_confirmed() FROM PUBLIC;

GRANT USAGE ON SCHEMA app TO app_api, app_worker, app_maintenance;
GRANT USAGE ON SCHEMA reporting TO app_api, app_maintenance, app_reporting;
GRANT EXECUTE ON FUNCTION app.system_user_id() TO app_api, app_maintenance;

-- ---- app_api: API web ----
GRANT SELECT ON ALL TABLES IN SCHEMA app TO app_api;
GRANT INSERT, UPDATE ON app.users, app.owner_profiles, app.listings, app.room_listings, app.job_listings,
    app.reviews, app.reports, app.appeals, app.owner_documents, app.criteria TO app_api;
GRANT INSERT, DELETE ON app.user_roles, app.room_amenities, app.saved_listings TO app_api;
GRANT INSERT, DELETE, UPDATE (sort_order) ON app.listing_images TO app_api;
GRANT INSERT ON app.listing_transitions, app.scans, app.scan_images, app.identifiers, app.scan_identifiers,
    app.document_access_logs TO app_api;
GRANT UPDATE (expires_at) ON app.scans TO app_api;                  -- giữ lại bằng chứng khi có báo cáo
GRANT INSERT, UPDATE (published_at) ON app.outbox_messages TO app_api;
GRANT UPDATE (is_active, activated_at) ON app.model_versions TO app_api;
GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO app_api;

-- ---- app_worker: AI worker (không đọc users, hồ sơ, giấy tờ, báo cáo) ----
GRANT SELECT ON app.scans, app.scan_images, app.listings, app.room_listings, app.job_listings,
    app.listing_images, app.administrative_units, app.criteria, app.model_versions, app.identifiers,
    app.identifier_stats, app.scan_identifiers, app.assessments, app.assessment_flags,
    app.assessment_image_matches, app.listing_embeddings TO app_worker;
GRANT SELECT (id, created_at) ON app.users TO app_worker;  -- chỉ tuổi tài khoản người đăng, không PII
GRANT INSERT ON app.assessments, app.assessment_flags, app.assessment_image_matches, app.identifiers,
    app.scan_identifiers, app.listing_embeddings TO app_worker;
GRANT UPDATE (status, completed_at, error_code) ON app.scans TO app_worker;
GRANT UPDATE (ocr_text) ON app.scan_images TO app_worker;
GRANT UPDATE (phash, clip_embedding, clip_model_id) ON app.listing_images TO app_worker;
GRANT UPDATE (embedding, model_version_id, updated_at) ON app.listing_embeddings TO app_worker;

-- ---- app_maintenance: job hằng đêm ----
GRANT SELECT, DELETE ON app.scans, app.outbox_messages TO app_maintenance;
GRANT SELECT ON app.scan_images, app.reports, app.appeals, app.listing_transitions TO app_maintenance;
GRANT SELECT, UPDATE (status, version, updated_at) ON app.listings TO app_maintenance;
GRANT INSERT ON app.listing_transitions TO app_maintenance;
GRANT MAINTAIN ON reporting.daily_scans, reporting.daily_moderation, reporting.risk_distribution
    TO app_maintenance;  -- PostgreSQL 17+: cho phép REFRESH MATERIALIZED VIEW mà không cần là chủ sở hữu

-- ---- app_reporting: đọc thống kê ----
GRANT SELECT ON ALL TABLES IN SCHEMA reporting TO app_reporting;
