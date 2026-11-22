-- =============================================================================
-- 0005_rental_core.sql — quản lý trọ, Giai đoạn 1 của nền tảng (docs/12-rental.md)
--   Nhà, phòng, tài khoản nhận tiền, hợp đồng, hóa đơn, ghi nhận thanh toán.
--   Thêm vai trò renter, quyền rental.manage / rental.tenant, danh mục ngân hàng.
--   Tin đăng sinh từ phòng: room_listings.room_id.
-- Hệ thống KHÔNG nhận, giữ hay chuyển tiền: payments chỉ là dữ liệu chủ trọ khai.
-- Chạy bằng role app_migrator. KHÔNG sửa file này sau khi đã chạy ở bất kỳ môi trường nào.
-- =============================================================================

SET search_path = app, public;

-- btree_gist là trusted extension (PostgreSQL ≥ 13) nên app_migrator (có CREATE trên database) tự tạo được,
-- không cần sửa db/init/00_init.sh. Đặt trong schema app vì app_migrator không có CREATE trên public.
-- Cần cho EXCLUDE (room_id WITH =, daterange WITH &&) của leases.
CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA app;

-- -----------------------------------------------------------------------------
-- Kiểu
-- -----------------------------------------------------------------------------
CREATE TYPE app.room_status           AS ENUM ('vacant', 'reserved', 'occupied', 'maintenance');
CREATE TYPE app.lease_status          AS ENUM ('draft', 'awaiting_signatures', 'active', 'ended', 'cancelled');
CREATE TYPE app.lease_party_role      AS ENUM ('landlord', 'tenant');
CREATE TYPE app.invoice_status        AS ENUM ('draft', 'issued', 'paid', 'void');
CREATE TYPE app.invoice_line_kind     AS ENUM ('rent', 'electricity', 'water', 'service', 'other');
CREATE TYPE app.payment_method        AS ENUM ('bank_transfer', 'cash');
CREATE TYPE app.payout_account_status AS ENUM ('active', 'flagged');

-- -----------------------------------------------------------------------------
-- Danh mục ngân hàng (mã BIN của NAPAS, dùng trong chuỗi VietQR)
-- -----------------------------------------------------------------------------
CREATE TABLE app.banks (
    id         SMALLINT GENERATED ALWAYS AS IDENTITY,
    bin        CHAR(6)      NOT NULL,
    short_name VARCHAR(30)  NOT NULL,
    name       VARCHAR(150) NOT NULL,
    is_active  BOOLEAN      NOT NULL DEFAULT true,
    CONSTRAINT pk_banks PRIMARY KEY (id),
    CONSTRAINT uq_banks_bin UNIQUE (bin),
    CONSTRAINT ck_banks_bin CHECK (bin ~ '^[0-9]{6}$'),
    CONSTRAINT ck_banks_short_name_not_blank CHECK (btrim(short_name) <> ''),
    CONSTRAINT ck_banks_name_not_blank CHECK (btrim(name) <> '')
);
COMMENT ON TABLE app.banks IS 'Ngân hàng nhận chuyển khoản VietQR (mã BIN NAPAS).';

-- -----------------------------------------------------------------------------
-- Tài khoản nhận tiền của chủ trọ (1-1 với users)
-- -----------------------------------------------------------------------------
CREATE TABLE app.payout_accounts (
    owner_id           BIGINT                    NOT NULL,
    bank_id            SMALLINT                  NOT NULL,
    account_ciphertext BYTEA                     NOT NULL,  -- nonce ‖ ciphertext ‖ tag (AES-256-GCM, AAD payout-account:{owner_id})
    key_id             VARCHAR(20)               NOT NULL,  -- khóa trong Crypto__DocumentKeys
    account_hmac       BYTEA                     NOT NULL,  -- HMAC-SHA256 của số đã chuẩn hóa, đối chiếu với identifiers
    account_hint       VARCHAR(10)               NOT NULL,  -- "•••1234"
    holder_name        VARCHAR(50)               NOT NULL,
    status             app.payout_account_status NOT NULL DEFAULT 'active',
    reviewed_by        BIGINT,                   -- NULL: chưa cán bộ nào xem xét
    reviewed_at        TIMESTAMPTZ,              -- NULL: chưa cán bộ nào xem xét
    review_note        VARCHAR(500),             -- NULL: chưa cán bộ nào xem xét
    updated_at         TIMESTAMPTZ               NOT NULL DEFAULT now(),
    CONSTRAINT pk_payout_accounts PRIMARY KEY (owner_id),
    CONSTRAINT fk_payout_accounts_owner_id FOREIGN KEY (owner_id) REFERENCES app.users (id),
    CONSTRAINT fk_payout_accounts_bank_id FOREIGN KEY (bank_id) REFERENCES app.banks (id),
    CONSTRAINT fk_payout_accounts_reviewed_by FOREIGN KEY (reviewed_by) REFERENCES app.users (id),
    CONSTRAINT ck_payout_accounts_hmac_len CHECK (octet_length(account_hmac) = 32),
    CONSTRAINT ck_payout_accounts_holder CHECK (holder_name ~ '^[A-Z][A-Z ]*$' AND btrim(holder_name) = holder_name),
    CONSTRAINT ck_payout_accounts_hint_not_blank CHECK (btrim(account_hint) <> ''),
    CONSTRAINT ck_payout_accounts_reviewed CHECK (
        num_nulls(reviewed_by, reviewed_at, review_note) IN (0, 3)),
    CONSTRAINT ck_payout_accounts_not_self_reviewed CHECK (reviewed_by IS NULL OR reviewed_by <> owner_id)
);
CREATE INDEX ix_payout_accounts_flagged ON app.payout_accounts (updated_at) WHERE status = 'flagged';
CREATE TRIGGER trg_payout_accounts_updated_at BEFORE UPDATE ON app.payout_accounts
    FOR EACH ROW EXECUTE FUNCTION app.set_updated_at();
COMMENT ON TABLE app.payout_accounts IS 'Tài khoản nhận tiền thuê của chủ trọ; số tài khoản chỉ lưu dạng mã hóa + HMAC.';

-- -----------------------------------------------------------------------------
-- Nhà và phòng
-- -----------------------------------------------------------------------------
CREATE TABLE app.properties (
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    owner_id       BIGINT                        NOT NULL,
    name           VARCHAR(150)                  NOT NULL,
    ward_code      VARCHAR(10)                   NOT NULL,
    street_address VARCHAR(200)                  NOT NULL,
    location       public.geography(Point, 4326) NOT NULL,
    note           VARCHAR(1000),                -- NULL: không có ghi chú
    created_at     TIMESTAMPTZ                   NOT NULL DEFAULT now(),
    CONSTRAINT pk_properties PRIMARY KEY (id),
    CONSTRAINT fk_properties_owner_id FOREIGN KEY (owner_id) REFERENCES app.users (id),
    CONSTRAINT fk_properties_ward_code FOREIGN KEY (ward_code) REFERENCES app.administrative_units (code),
    CONSTRAINT ck_properties_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT ck_properties_street_address_not_blank CHECK (btrim(street_address) <> ''),
    CONSTRAINT ck_properties_note_not_blank CHECK (note IS NULL OR btrim(note) <> '')
);
CREATE INDEX ix_properties_owner_id ON app.properties (owner_id);
COMMENT ON TABLE app.properties IS 'Nhà trọ / tòa nhà của một chủ.';

CREATE TABLE app.rooms (
    id                BIGINT GENERATED ALWAYS AS IDENTITY,
    property_id       BIGINT          NOT NULL,
    code              VARCHAR(20)     NOT NULL,
    floor             SMALLINT,       -- NULL: không ghi tầng
    area_m2           NUMERIC(5,1)    NOT NULL,
    is_self_contained BOOLEAN         NOT NULL,
    list_rent_vnd     INTEGER         NOT NULL,
    status            app.room_status NOT NULL DEFAULT 'vacant',
    version           INTEGER         NOT NULL DEFAULT 1,
    created_at        TIMESTAMPTZ     NOT NULL DEFAULT now(),
    CONSTRAINT pk_rooms PRIMARY KEY (id),
    CONSTRAINT fk_rooms_property_id FOREIGN KEY (property_id) REFERENCES app.properties (id),
    CONSTRAINT uq_rooms_property_code UNIQUE (property_id, code),
    CONSTRAINT ck_rooms_code_not_blank CHECK (btrim(code) <> ''),
    CONSTRAINT ck_rooms_floor CHECK (floor BETWEEN -5 AND 100),
    CONSTRAINT ck_rooms_area CHECK (area_m2 > 0 AND area_m2 <= 500),
    CONSTRAINT ck_rooms_list_rent CHECK (list_rent_vnd BETWEEN 100000 AND 100000000),
    CONSTRAINT ck_rooms_version_positive CHECK (version > 0)
);
COMMENT ON TABLE app.rooms IS 'Từng phòng; reserved/occupied chỉ do hợp đồng đặt (docs/12-rental.md, US-1002).';

CREATE TABLE app.rental_room_amenities (
    room_id    BIGINT   NOT NULL,
    amenity_id SMALLINT NOT NULL,
    CONSTRAINT pk_rental_room_amenities PRIMARY KEY (room_id, amenity_id),
    CONSTRAINT fk_rental_room_amenities_room_id FOREIGN KEY (room_id) REFERENCES app.rooms (id) ON DELETE CASCADE,
    CONSTRAINT fk_rental_room_amenities_amenity_id FOREIGN KEY (amenity_id) REFERENCES app.amenities (id)
);
CREATE INDEX ix_rental_room_amenities_amenity_id ON app.rental_room_amenities (amenity_id);
COMMENT ON TABLE app.rental_room_amenities IS 'Bảng nối N-N phòng – tiện nghi (dùng chung danh mục amenities).';

-- Tin sinh từ phòng trống (BR-32). NULL: tin đăng tay, không gắn phòng.
ALTER TABLE app.room_listings ADD COLUMN room_id BIGINT;
ALTER TABLE app.room_listings
    ADD CONSTRAINT fk_room_listings_room_id FOREIGN KEY (room_id) REFERENCES app.rooms (id);
CREATE INDEX ix_room_listings_room_id ON app.room_listings (room_id) WHERE room_id IS NOT NULL;
COMMENT ON COLUMN app.room_listings.room_id IS
    'Phòng sinh ra tin này; NULL: tin đăng tay. Mỗi phòng tối đa một tin chưa removed (API kiểm, BR-32).';

-- -----------------------------------------------------------------------------
-- Hợp đồng
-- -----------------------------------------------------------------------------
CREATE TABLE app.leases (
    id                    BIGINT GENERATED ALWAYS AS IDENTITY,
    public_id             UUID             NOT NULL DEFAULT gen_random_uuid(),
    room_id               BIGINT           NOT NULL,
    status                app.lease_status NOT NULL DEFAULT 'draft',
    start_date            DATE             NOT NULL,
    end_date              DATE             NOT NULL,
    rent_vnd              INTEGER          NOT NULL,
    deposit_vnd           INTEGER          NOT NULL,
    billing_day           SMALLINT         NOT NULL,
    electricity_price_vnd INTEGER          NOT NULL,  -- đ/kWh
    water_price_vnd       INTEGER          NOT NULL,  -- đ/m³ hoặc đ/người tùy water_unit
    water_unit            VARCHAR(10)      NOT NULL,
    services              JSONB            NOT NULL DEFAULT '[]',  -- [{"label": "...", "amountVnd": 100000}]
    extra_terms           VARCHAR(3000),   -- NULL: không có điều khoản thêm
    content_sha256        BYTEA,           -- NULL: chưa gửi (draft) hoặc hủy khi còn nháp
    sent_at               TIMESTAMPTZ,     -- NULL: chưa gửi
    invite_expires_at     TIMESTAMPTZ,     -- NULL: chưa gửi
    activated_at          TIMESTAMPTZ,     -- NULL: chưa đủ các bên xác nhận
    ended_at              TIMESTAMPTZ,     -- NULL: chưa kết thúc
    cancelled_at          TIMESTAMPTZ,     -- NULL: chưa hủy
    version               INTEGER          NOT NULL DEFAULT 1,
    created_at            TIMESTAMPTZ      NOT NULL DEFAULT now(),
    CONSTRAINT pk_leases PRIMARY KEY (id),
    CONSTRAINT uq_leases_public_id UNIQUE (public_id),
    CONSTRAINT fk_leases_room_id FOREIGN KEY (room_id) REFERENCES app.rooms (id),
    -- BR-22: một phòng không có hai hợp đồng đang chờ ký / hiệu lực chồng thời gian; nối tiếp [) được phép.
    CONSTRAINT ex_leases_room_period EXCLUDE USING gist (
        room_id WITH =, daterange(start_date, end_date, '[)') WITH &&)
        WHERE (status IN ('awaiting_signatures', 'active')),
    CONSTRAINT ck_leases_dates CHECK (start_date < end_date),
    CONSTRAINT ck_leases_duration CHECK (
        end_date >= start_date + interval '1 month' AND end_date <= start_date + interval '36 months'),
    CONSTRAINT ck_leases_rent CHECK (rent_vnd BETWEEN 100000 AND 100000000),
    CONSTRAINT ck_leases_deposit CHECK (deposit_vnd BETWEEN 0 AND 1000000000),
    CONSTRAINT ck_leases_billing_day CHECK (billing_day BETWEEN 1 AND 28),
    CONSTRAINT ck_leases_utility_prices CHECK (
        electricity_price_vnd BETWEEN 0 AND 100000 AND water_price_vnd BETWEEN 0 AND 1000000),
    CONSTRAINT ck_leases_water_unit CHECK (water_unit IN ('m3', 'person')),
    CONSTRAINT ck_leases_services_array CHECK (jsonb_typeof(services) = 'array'),
    CONSTRAINT ck_leases_extra_terms_not_blank CHECK (extra_terms IS NULL OR btrim(extra_terms) <> ''),
    CONSTRAINT ck_leases_sha_len CHECK (content_sha256 IS NULL OR octet_length(content_sha256) = 32),
    -- BR-24: đã gửi thì nội dung được chốt; nháp thì chưa có gì của lần gửi.
    CONSTRAINT ck_leases_sent CHECK (
        num_nulls(content_sha256, sent_at, invite_expires_at) IN (0, 3)
        AND (status <> 'draft' OR sent_at IS NULL)
        AND (status NOT IN ('awaiting_signatures', 'active', 'ended') OR sent_at IS NOT NULL)),
    CONSTRAINT ck_leases_activated CHECK ((status IN ('active', 'ended')) = (activated_at IS NOT NULL)),
    CONSTRAINT ck_leases_ended CHECK ((status = 'ended') = (ended_at IS NOT NULL)),
    CONSTRAINT ck_leases_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL)),
    CONSTRAINT ck_leases_version_positive CHECK (version > 0)
);
CREATE INDEX ix_leases_room_id ON app.leases (room_id);
CREATE INDEX ix_leases_awaiting_expiry ON app.leases (invite_expires_at) WHERE status = 'awaiting_signatures';
CREATE INDEX ix_leases_active_end ON app.leases (end_date) WHERE status = 'active';
COMMENT ON TABLE app.leases IS 'Hợp đồng thuê; hai bên xác nhận bằng OTP email trên content_sha256 (không phải chữ ký số).';

CREATE TABLE app.lease_parties (
    id                BIGINT GENERATED ALWAYS AS IDENTITY,
    lease_id          BIGINT               NOT NULL,
    role              app.lease_party_role NOT NULL,
    user_id           BIGINT,              -- NULL: bên thuê chưa nhận lời mời
    invite_email      VARCHAR(254),        -- NULL: bên cho thuê (không mời)
    invite_token_hash BYTEA,               -- SHA-256 của token mời; NULL: bên cho thuê hoặc chưa gửi
    claimed_at        TIMESTAMPTZ,         -- NULL: chưa nhận lời mời (bên thuê) / bên cho thuê
    confirmed_at      TIMESTAMPTZ,         -- NULL: chưa xác nhận bằng OTP
    display_name      VARCHAR(100),        -- NULL: dùng users.full_name; khác NULL khi tài khoản đã ẩn danh hóa
    CONSTRAINT pk_lease_parties PRIMARY KEY (id),
    CONSTRAINT fk_lease_parties_lease_id FOREIGN KEY (lease_id) REFERENCES app.leases (id) ON DELETE CASCADE,
    CONSTRAINT fk_lease_parties_user_id FOREIGN KEY (user_id) REFERENCES app.users (id),
    CONSTRAINT uq_lease_parties_lease_user UNIQUE (lease_id, user_id),
    CONSTRAINT ck_lease_parties_landlord CHECK (
        role <> 'landlord' OR (user_id IS NOT NULL AND invite_email IS NULL AND invite_token_hash IS NULL
                               AND claimed_at IS NULL)),
    CONSTRAINT ck_lease_parties_tenant CHECK (
        role <> 'tenant' OR (invite_email IS NOT NULL AND (user_id IS NULL) = (claimed_at IS NULL))),
    CONSTRAINT ck_lease_parties_email_format CHECK (
        invite_email IS NULL OR invite_email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'),
    CONSTRAINT ck_lease_parties_token_len CHECK (invite_token_hash IS NULL OR octet_length(invite_token_hash) = 32),
    CONSTRAINT ck_lease_parties_confirmed CHECK (confirmed_at IS NULL OR user_id IS NOT NULL),
    CONSTRAINT ck_lease_parties_display_name_not_blank CHECK (display_name IS NULL OR btrim(display_name) <> '')
);
CREATE UNIQUE INDEX uq_lease_parties_one_landlord ON app.lease_parties (lease_id) WHERE role = 'landlord';
CREATE UNIQUE INDEX uq_lease_parties_lease_email ON app.lease_parties (lease_id, lower(invite_email))
    WHERE invite_email IS NOT NULL;
CREATE UNIQUE INDEX uq_lease_parties_invite_token ON app.lease_parties (invite_token_hash)
    WHERE invite_token_hash IS NOT NULL;
CREATE INDEX ix_lease_parties_user_id ON app.lease_parties (user_id) WHERE user_id IS NOT NULL;
COMMENT ON TABLE app.lease_parties IS 'Các bên của hợp đồng: đúng một bên cho thuê (chủ phòng), 1-4 bên thuê.';

-- BR-23: bên cho thuê là chủ nhà của phòng; bên thuê không phải chủ phòng; tối đa 4 bên thuê.
CREATE FUNCTION app.check_lease_party() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_owner BIGINT;
BEGIN
    SELECT p.owner_id INTO v_owner
    FROM app.leases l
    JOIN app.rooms r ON r.id = l.room_id
    JOIN app.properties p ON p.id = r.property_id
    WHERE l.id = NEW.lease_id;

    IF NEW.role = 'landlord' AND NEW.user_id IS DISTINCT FROM v_owner THEN
        RAISE EXCEPTION 'rental.invalid_party' USING DETAIL = 'landlord must own the room';
    END IF;
    IF NEW.role = 'tenant' AND NEW.user_id = v_owner THEN
        RAISE EXCEPTION 'rental.invalid_party' USING DETAIL = 'tenant cannot be the landlord';
    END IF;
    IF NEW.role = 'tenant' AND TG_OP = 'INSERT' AND (
        SELECT count(*) FROM app.lease_parties lp WHERE lp.lease_id = NEW.lease_id AND lp.role = 'tenant') >= 4 THEN
        RAISE EXCEPTION 'rental.invalid_party' USING DETAIL = 'at most 4 tenants';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trg_lease_parties_check BEFORE INSERT OR UPDATE OF role, user_id ON app.lease_parties
    FOR EACH ROW EXECUTE FUNCTION app.check_lease_party();

-- -----------------------------------------------------------------------------
-- Hóa đơn
-- -----------------------------------------------------------------------------
CREATE TABLE app.invoices (
    id                 BIGINT GENERATED ALWAYS AS IDENTITY,
    public_id          UUID               NOT NULL DEFAULT gen_random_uuid(),
    lease_id           BIGINT             NOT NULL,
    period             CHAR(7)            NOT NULL,  -- yyyy-MM theo giờ Việt Nam
    status             app.invoice_status NOT NULL DEFAULT 'draft',
    total_vnd          INTEGER            NOT NULL,
    -- Bản chụp lúc phát hành (US-1202). Cả nhóm NULL: chưa phát hành (draft) hoặc hủy khi còn nháp.
    due_date           DATE,
    transfer_code      CHAR(10),
    bank_id            SMALLINT,
    account_ciphertext BYTEA,             -- AAD invoice:{id}
    key_id             VARCHAR(20),
    account_hint       VARCHAR(10),
    holder_name        VARCHAR(50),
    issued_at          TIMESTAMPTZ,
    paid_at            TIMESTAMPTZ,       -- NULL: chưa thu đủ
    voided_at          TIMESTAMPTZ,       -- NULL: chưa hủy
    void_reason        VARCHAR(300),      -- NULL: chưa hủy
    version            INTEGER            NOT NULL DEFAULT 1,
    created_at         TIMESTAMPTZ        NOT NULL DEFAULT now(),
    CONSTRAINT pk_invoices PRIMARY KEY (id),
    CONSTRAINT uq_invoices_public_id UNIQUE (public_id),
    CONSTRAINT fk_invoices_lease_id FOREIGN KEY (lease_id) REFERENCES app.leases (id),
    CONSTRAINT fk_invoices_bank_id FOREIGN KEY (bank_id) REFERENCES app.banks (id),
    CONSTRAINT ck_invoices_period CHECK (period ~ '^[0-9]{4}-(0[1-9]|1[0-2])$'),
    CONSTRAINT ck_invoices_total CHECK (total_vnd BETWEEN 0 AND 2000000000),
    CONSTRAINT ck_invoices_transfer_code CHECK (transfer_code IS NULL OR transfer_code ~ '^TT[0-9A-HJKMNP-TV-Z]{8}$'),
    CONSTRAINT ck_invoices_snapshot CHECK (
        num_nulls(due_date, transfer_code, bank_id, account_ciphertext, key_id, account_hint, holder_name,
                  issued_at) IN (0, 8)
        AND (status <> 'draft' OR issued_at IS NULL)
        AND (status NOT IN ('issued', 'paid') OR issued_at IS NOT NULL)),
    CONSTRAINT ck_invoices_paid CHECK ((status = 'paid') = (paid_at IS NOT NULL)),
    CONSTRAINT ck_invoices_void CHECK (
        (status = 'void') = (voided_at IS NOT NULL)
        AND (voided_at IS NULL) = (void_reason IS NULL)
        AND (void_reason IS NULL OR btrim(void_reason) <> '')),
    CONSTRAINT ck_invoices_version_positive CHECK (version > 0)
);
-- BR-28: mỗi hợp đồng tối đa một hóa đơn chưa hủy cho mỗi kỳ.
CREATE UNIQUE INDEX uq_invoices_lease_period ON app.invoices (lease_id, period) WHERE status <> 'void';
CREATE UNIQUE INDEX uq_invoices_transfer_code ON app.invoices (transfer_code) WHERE transfer_code IS NOT NULL;
CREATE INDEX ix_invoices_issued_due ON app.invoices (due_date) WHERE status = 'issued';
COMMENT ON TABLE app.invoices IS 'Hóa đơn tháng; đã phát hành thì không sửa, chỉ hủy rồi lập lại (BR-28).';

CREATE TABLE app.invoice_lines (
    id             BIGINT GENERATED ALWAYS AS IDENTITY,
    invoice_id     BIGINT                NOT NULL,
    kind           app.invoice_line_kind NOT NULL,
    label          VARCHAR(100)          NOT NULL,
    meter_start    NUMERIC(10,1),        -- NULL: dòng không phải điện / nước theo đồng hồ
    meter_end      NUMERIC(10,1),        -- NULL: như meter_start
    quantity       NUMERIC(10,1)         NOT NULL,
    unit_price_vnd INTEGER               NOT NULL,
    amount_vnd     INTEGER               NOT NULL,
    sort_order     SMALLINT              NOT NULL,
    CONSTRAINT pk_invoice_lines PRIMARY KEY (id),
    CONSTRAINT fk_invoice_lines_invoice_id FOREIGN KEY (invoice_id) REFERENCES app.invoices (id) ON DELETE CASCADE,
    CONSTRAINT uq_invoice_lines_invoice_sort UNIQUE (invoice_id, sort_order),
    CONSTRAINT ck_invoice_lines_label_not_blank CHECK (btrim(label) <> ''),
    CONSTRAINT ck_invoice_lines_meter CHECK (
        (meter_start IS NULL) = (meter_end IS NULL)
        AND (meter_start IS NULL OR (kind IN ('electricity', 'water') AND meter_start >= 0 AND meter_end >= meter_start))),
    CONSTRAINT ck_invoice_lines_quantity CHECK (quantity >= 0),
    CONSTRAINT ck_invoice_lines_amount CHECK (unit_price_vnd >= 0 AND amount_vnd >= 0)
);
COMMENT ON TABLE app.invoice_lines IS 'Dòng hóa đơn: tiền phòng, điện, nước (kèm chỉ số), dịch vụ, khoản khác.';

-- Dòng chỉ đổi được khi hóa đơn còn nháp (BR-28).
CREATE FUNCTION app.check_invoice_line_editable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_status app.invoice_status;
BEGIN
    SELECT status INTO v_status FROM app.invoices
    WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.invoice_id ELSE NEW.invoice_id END;
    -- Hóa đơn đã bị xóa cùng transaction (CASCADE của job lưu giữ): không còn gì để giữ.
    IF v_status IS NOT NULL AND v_status <> 'draft' THEN
        RAISE EXCEPTION 'rental.invoice_not_editable';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$;
CREATE TRIGGER trg_invoice_lines_editable BEFORE INSERT OR UPDATE OR DELETE ON app.invoice_lines
    FOR EACH ROW EXECUTE FUNCTION app.check_invoice_line_editable();

-- Khi hóa đơn rời draft: tổng các dòng phải bằng total_vnd (kiểm lúc COMMIT để API ghi dòng rồi mới phát hành).
CREATE FUNCTION app.check_invoice_total() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.status <> 'draft' AND NEW.total_vnd IS DISTINCT FROM (
        SELECT coalesce(sum(amount_vnd), 0) FROM app.invoice_lines WHERE invoice_id = NEW.id) THEN
        RAISE EXCEPTION 'rental.invoice_total_mismatch';
    END IF;
    RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER trg_invoices_total AFTER INSERT OR UPDATE OF status, total_vnd ON app.invoices
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION app.check_invoice_total();

-- -----------------------------------------------------------------------------
-- Ghi nhận thanh toán (chỉ thêm; sửa sai bằng dòng đảo)
-- -----------------------------------------------------------------------------
CREATE TABLE app.payments (
    id                  BIGINT GENERATED ALWAYS AS IDENTITY,
    invoice_id          BIGINT             NOT NULL,
    amount_vnd          INTEGER            NOT NULL,
    paid_on             DATE               NOT NULL,
    method              app.payment_method NOT NULL,
    note                VARCHAR(300),      -- NULL: không ghi chú
    reverses_payment_id BIGINT,            -- NULL: lần thu thường; khác NULL: dòng đảo lần thu này
    recorded_by         BIGINT             NOT NULL,
    recorded_at         TIMESTAMPTZ        NOT NULL DEFAULT now(),
    CONSTRAINT pk_payments PRIMARY KEY (id),
    CONSTRAINT fk_payments_invoice_id FOREIGN KEY (invoice_id) REFERENCES app.invoices (id) ON DELETE CASCADE,
    CONSTRAINT fk_payments_reverses_payment_id FOREIGN KEY (reverses_payment_id)
        REFERENCES app.payments (id) ON DELETE CASCADE,
    CONSTRAINT fk_payments_recorded_by FOREIGN KEY (recorded_by) REFERENCES app.users (id),
    CONSTRAINT uq_payments_reverses_payment_id UNIQUE (reverses_payment_id),
    CONSTRAINT ck_payments_amount CHECK (amount_vnd > 0),
    CONSTRAINT ck_payments_note_not_blank CHECK (note IS NULL OR btrim(note) <> '')
);
CREATE INDEX ix_payments_invoice_id ON app.payments (invoice_id);
COMMENT ON TABLE app.payments IS 'Chủ trọ ghi nhận đã thu (hệ thống không nhận tiền). Chỉ thêm: ghi sai thì thêm dòng đảo.';

-- BR-30: chỉ ghi trên hóa đơn issued/paid; dòng đảo cùng hóa đơn, cùng số tiền, không đảo dòng đảo;
-- số thu ròng (thường − đảo) nằm trong [0, total_vnd].
CREATE FUNCTION app.check_payment() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_status app.invoice_status;
    v_total  INTEGER;
    v_net    BIGINT;
    v_orig   app.payments%ROWTYPE;
BEGIN
    SELECT status, total_vnd INTO v_status, v_total FROM app.invoices WHERE id = NEW.invoice_id FOR UPDATE;
    IF v_status NOT IN ('issued', 'paid') THEN
        RAISE EXCEPTION 'rental.invoice_not_payable';
    END IF;
    IF NEW.reverses_payment_id IS NOT NULL THEN
        SELECT * INTO v_orig FROM app.payments WHERE id = NEW.reverses_payment_id;
        IF v_orig.invoice_id <> NEW.invoice_id OR v_orig.reverses_payment_id IS NOT NULL
           OR v_orig.amount_vnd <> NEW.amount_vnd THEN
            RAISE EXCEPTION 'rental.invalid_reversal';
        END IF;
    END IF;
    SELECT coalesce(sum(CASE WHEN reverses_payment_id IS NULL THEN amount_vnd ELSE -amount_vnd END), 0)
    INTO v_net FROM app.payments WHERE invoice_id = NEW.invoice_id;
    v_net := v_net + CASE WHEN NEW.reverses_payment_id IS NULL THEN NEW.amount_vnd ELSE -NEW.amount_vnd END;
    IF v_net > v_total THEN
        RAISE EXCEPTION 'rental.payment_exceeds_total';
    END IF;
    RETURN NEW;
END
$$;
CREATE TRIGGER trg_payments_check BEFORE INSERT ON app.payments
    FOR EACH ROW EXECUTE FUNCTION app.check_payment();

-- -----------------------------------------------------------------------------
-- Dữ liệu tham chiếu
-- -----------------------------------------------------------------------------
INSERT INTO app.roles (code, name) VALUES ('renter', 'Người thuê');

INSERT INTO app.permissions (code, description) VALUES
    ('rental.manage', 'Quản lý nhà, phòng, hợp đồng, hóa đơn, tài khoản nhận tiền của mình'),
    ('rental.tenant', 'Xem và xác nhận hợp đồng, hóa đơn mà mình là bên thuê');

INSERT INTO app.role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM (VALUES
    ('owner', 'rental.manage'),
    ('student', 'rental.tenant'),
    ('renter', 'rental.tenant'), ('renter', 'scan.create'), ('renter', 'report.create'),
    ('renter', 'listing.save')
) AS m (role_code, permission_code)
JOIN app.roles r ON r.code = m.role_code
JOIN app.permissions p ON p.code = m.permission_code;

-- Mã BIN theo danh sách ngân hàng thành viên NAPAS (kiểm 11/2026).
INSERT INTO app.banks (bin, short_name, name) VALUES
    ('970436', 'Vietcombank', 'Ngân hàng TMCP Ngoại thương Việt Nam'),
    ('970415', 'VietinBank',  'Ngân hàng TMCP Công thương Việt Nam'),
    ('970418', 'BIDV',        'Ngân hàng TMCP Đầu tư và Phát triển Việt Nam'),
    ('970405', 'Agribank',    'Ngân hàng Nông nghiệp và Phát triển Nông thôn Việt Nam'),
    ('970422', 'MB',          'Ngân hàng TMCP Quân đội'),
    ('970407', 'Techcombank', 'Ngân hàng TMCP Kỹ thương Việt Nam'),
    ('970416', 'ACB',         'Ngân hàng TMCP Á Châu'),
    ('970432', 'VPBank',      'Ngân hàng TMCP Việt Nam Thịnh Vượng'),
    ('970423', 'TPBank',      'Ngân hàng TMCP Tiên Phong'),
    ('970403', 'Sacombank',   'Ngân hàng TMCP Sài Gòn Thương Tín');

-- -----------------------------------------------------------------------------
-- Quyền (quy tắc: mỗi bảng mới GRANT tường minh ngay trong migration tạo nó)
-- -----------------------------------------------------------------------------
REVOKE EXECUTE ON FUNCTION app.check_lease_party(), app.check_invoice_line_editable(), app.check_invoice_total(),
    app.check_payment() FROM PUBLIC;

GRANT SELECT ON app.banks, app.payout_accounts, app.properties, app.rooms, app.rental_room_amenities, app.leases,
    app.lease_parties, app.invoices, app.invoice_lines, app.payments TO app_api;
GRANT INSERT, UPDATE ON app.payout_accounts, app.properties, app.rooms, app.leases, app.lease_parties,
    app.invoices, app.invoice_lines TO app_api;
GRANT INSERT, DELETE ON app.rental_room_amenities TO app_api;
GRANT DELETE ON app.lease_parties, app.invoice_lines TO app_api;   -- sửa nháp hợp đồng / hóa đơn
GRANT INSERT ON app.payments TO app_api;                           -- chỉ thêm: không UPDATE/DELETE
GRANT SELECT, INSERT, UPDATE ON app.room_listings TO app_api;      -- cột room_id mới (đã phủ bởi 0001, cấp lại tường minh)

-- Job đêm (docs/12-rental.md, BR-26, BR-33, BR-34): hủy hợp đồng quá hạn mời, kết thúc hợp đồng quá end_date,
-- trả phòng về vacant, email nhắc, xóa dữ liệu quá 2 năm sau ngày kết thúc (xóa leases kéo theo CASCADE
-- lease_parties; invoices xóa trước, kéo theo invoice_lines và payments).
GRANT SELECT ON app.properties, app.rooms, app.leases, app.lease_parties, app.invoices, app.invoice_lines,
    app.payments TO app_maintenance;
GRANT UPDATE (status, ended_at, cancelled_at, version) ON app.leases TO app_maintenance;
GRANT UPDATE (status, version) ON app.rooms TO app_maintenance;
GRANT DELETE ON app.leases, app.lease_parties, app.invoices, app.invoice_lines, app.payments TO app_maintenance;
GRANT SELECT (id, email, full_name, status) ON app.users TO app_maintenance;
