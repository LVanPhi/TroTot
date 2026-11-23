# 12 — Quản lý trọ (Giai đoạn 1 của nền tảng)

> Đặc tả cho **Giai đoạn 1** trong [11-product-vision.md](11-product-vision.md#lộ-trình). Phần này làm **sau buổi bảo vệ**:
> không task nào của T0–T5 phụ thuộc file này, và chỉ tiêu, phạm vi chấm của đồ án không đổi. Khi làm, file này là nguồn
> sự thật ngang hàng với 01/03/04/06/07; các file đó chỉ giữ tham chiếu tới đây.
>
> Giai đoạn 2 (lịch xem phòng, bàn giao, chỉ số đồng hồ bằng ảnh, đánh giá gắn hợp đồng) và Giai đoạn 3 (Enterprise
> nhiều tổ chức, API đối tác) chưa được đặc tả chi tiết; xem 11-product-vision.md.

## Phạm vi

| Trong Giai đoạn 1 | Chưa làm |
|---|---|
| Tòa nhà, phòng, trạng thái phòng | Lịch xem phòng, biên bản bàn giao có ảnh (GĐ 2) |
| Tài khoản nhận tiền của chủ trọ (mã hóa) + cảnh báo khi số này có báo cáo đã xác nhận | Đối soát ngân hàng tự động |
| Hợp đồng thuê: chủ trọ soạn, mời người thuê qua email, hai bên xác nhận bằng OTP email | Chữ ký số; tệp PDF do máy chủ tạo |
| Hóa đơn tháng: tiền phòng, điện, nước, dịch vụ; chỉ số nhập tay | Đọc chỉ số từ ảnh (GĐ 2) |
| Mã VietQR tới tài khoản chủ trọ; chủ trọ ghi nhận đã thu | Nhận, giữ, chuyển tiền dưới bất kỳ hình thức nào |
| Sinh tin đăng nháp từ phòng trống; tin tự rời trang công khai khi phòng đã cho thuê | Đánh giá gắn hợp đồng (GĐ 2) |
| Vai trò mới `renter` cho người thuê không phải sinh viên | Nhân viên của chuỗi, nhiều tổ chức (GĐ 3) |
| Email nhắc hạn hợp đồng, nhắc hóa đơn quá hạn | Zalo ZNS, SMS, Web Push |

Hạ tầng không đổi: mọi thứ chạy trong `api` và `maintenance` hiện có, cùng CSDL và Redis, email qua Brevo/Mailpit.
Giai đoạn 1 không cần worker. Thư viện mới duy nhất là **QRCoder** (.NET, MIT) để vẽ mã QR thành SVG ở API (đã thêm
vào 02-architecture.md).

## Phân quyền

Migration `0005` thêm vai trò, quyền và gán theo bảng dưới. API kiểm tra quyền, không kiểm tra tên vai trò.

| Quyền | student | renter | owner | moderator | admin |
|---|:-:|:-:|:-:|:-:|:-:|
| `rental.manage` — Quản lý nhà, phòng, hợp đồng, hóa đơn, tài khoản nhận tiền của mình | — | — | ✓ | — | — |
| `rental.tenant` — Xem và xác nhận hợp đồng, hóa đơn mà mình là bên thuê | ✓ | ✓ | — | — | — |
| `scan.create`, `report.create`, `listing.save` | ✓ (như cũ) | ✓ | (như cũ) | (như cũ) | (như cũ) |
| `owner.verify` — thêm: xem xét tài khoản nhận tiền bị gắn cờ | — | — | — | ✓ | — |

- `renter` (Người thuê): đăng ký bằng bất kỳ email nào đã xác thực (`accountType: "renter"`). Vai trò này không có
  `listing.create` và `review.create`. Đánh giá vẫn dành cho sinh viên cho tới khi GĐ 2 gắn đánh giá với hợp đồng.
- Endpoint của chủ trọ cần `rental.manage` **và** chủ trọ phải sở hữu tài nguyên (gọi tắt là "chủ nhà").
- Endpoint của người thuê cần `rental.tenant` **và** người gọi phải là bên thuê của hợp đồng (gọi tắt là "bên thuê").
- Tài nguyên của người khác luôn trả `404` (quy tắc 12 của AGENTS.md).
- Một người có cả `owner` và `student` vẫn **không** được làm bên thuê trong hợp đồng của chính mình (BR-23).

## Quy tắc nghiệp vụ

Số tiếp nối 01-requirements.md.

| Mã | Quy tắc | Nơi bảo đảm |
|---|---|---|
| BR-21 | Chủ trọ chỉ gửi hợp đồng và phát hành hóa đơn khi đạt điều kiện đăng tin BR-02 (số điện thoại đã xác thực + hồ sơ chủ còn hạn hoặc email trường) **và** có tài khoản nhận tiền đang `active`. Tạo nhà, phòng, soạn nháp thì không cần | API |
| BR-22 | Mỗi phòng có tối đa một hợp đồng `awaiting_signatures` hoặc `active` tại mọi thời điểm. Ngày bắt đầu < ngày kết thúc, thời hạn 1–36 tháng. Tiền thuê 100.000–100.000.000, tiền cọc ≥ 0, ngày chốt hóa đơn 1–28 | `EXCLUDE` + CHECK |
| BR-23 | Hợp đồng có đúng một bên cho thuê (chủ phòng) và 1–4 bên thuê. Bên thuê không được là chủ phòng | CHECK + trigger `check_lease_party` |
| BR-24 | Gửi hợp đồng thì chốt nội dung: API tính `content_sha256` trên JSON chuẩn hóa (mục Mã băm hợp đồng). Từ lúc đó không sửa được nữa; muốn sửa thì hủy và soạn lại | API + trạng thái |
| BR-25 | Một bên xác nhận bằng OTP 6 số gửi tới **email đã xác thực của chính bên đó**. OTP có hạn 10 phút, sai tối đa 5 lần. Bên xác nhận gửi kèm `contentSha256` đã xem; lệch với bản đã chốt → `409 rental.lease_content_changed`. Đủ mọi bên xác nhận thì hợp đồng sang `active` và phòng sang `occupied` | API (Redis) |
| BR-26 | Lời mời bên thuê có hạn 7 ngày. Quá hạn mà chưa đủ xác nhận thì job đêm chuyển hợp đồng sang `cancelled` và phòng về `vacant` | Job đêm |
| BR-27 | Phản hồi khi mời người thuê **không cho biết** email đó đã có tài khoản hay chưa (chống dò email). Email mời luôn được gửi; người nhận đăng ký hoặc đăng nhập bằng đúng email đó rồi nhận lời mời | API |
| BR-28 | Hóa đơn: tổng các dòng phải bằng `total_vnd`. Hóa đơn đã phát hành không sửa được, chỉ hủy (`void`, kèm lý do) rồi lập hóa đơn mới. Mỗi hợp đồng có tối đa một hóa đơn chưa hủy cho mỗi kỳ (`yyyy-MM`) | Trigger + UNIQUE một phần |
| BR-29 | Chỉ số điện, nước của kỳ này không được nhỏ hơn chỉ số cuối của kỳ trước trong cùng hợp đồng. API điền sẵn chỉ số đầu kỳ từ hóa đơn chưa hủy gần nhất | API + CHECK trên dòng |
| BR-30 | Ghi nhận thanh toán: chỉ chủ phòng ghi, tổng các lần thu không vượt `total_vnd`; thu đủ thì hóa đơn sang `paid`. Hệ thống **không** nhận, giữ hay chuyển tiền; ảnh chụp chuyển khoản không phải bằng chứng | Trigger + API |
| BR-31 | Số tài khoản nhận tiền được mã hóa AES-256-GCM, đối chiếu bằng HMAC như `identifiers`. Nếu HMAC trùng một định danh có `identifier_stats.confirmed_report_count > 0` thì tài khoản bị gắn cờ `flagged`: hóa đơn không hiện mã QR, người thuê thấy cảnh báo trung tính, chủ trọ không phát hành được hóa đơn mới cho tới khi cán bộ xem xét. Cán bộ không xem xét tài khoản của chính mình (BR-19) | API |
| BR-32 | Phòng chỉ sinh được tin khi đang `vacant`. Mỗi phòng có tối đa một tin chưa `removed`. Tin sinh ra là bản nháp và đi qua sàng lọc, kiểm duyệt như mọi tin khác. Khi hợp đồng của phòng sang `active`, nếu tin đang `verified` thì hệ thống đưa tin về `draft` qua `ListingTransitionService`, lý do "Phòng đã cho thuê" | API + `ListingTransitionService` |
| BR-33 | Hợp đồng `active` sang `ended` khi chủ phòng kết thúc sớm (ngày kết thúc mới ≥ hôm nay) hoặc khi job đêm thấy đã qua `end_date`. Phòng về `vacant` | API + job đêm |
| BR-34 | Xóa tài khoản (US-106): chủ trọ còn hợp đồng `awaiting_signatures`/`active` → `409 me.active_lease`. Bên thuê xóa tài khoản thì dòng hợp đồng vẫn giữ (là chứng từ của bên kia), tên hiển thị thành "Người thuê đã xóa tài khoản". Hợp đồng và hóa đơn giữ tới 2 năm sau ngày kết thúc rồi job đêm xóa | API + job đêm |

## User story

### E10 — Nhà, phòng, tài khoản nhận tiền (chủ trọ)

**US-1001 (M) Quản lý nhà trọ** — tên ≤ 150 ký tự, phường/xã, địa chỉ, ghim bản đồ, ghi chú. Một chủ có nhiều nhà.
- AC1: Chỉ chủ nhà thấy và sửa; người khác gọi tới nhận `404`.

**US-1002 (M) Quản lý phòng** — mã phòng (duy nhất trong nhà, ≤ 20 ký tự), tầng, diện tích, khép kín, tiện nghi
(dùng danh mục `amenities` sẵn có), giá niêm yết. Trạng thái `vacant` / `reserved` / `occupied` / `maintenance`.
- AC1: Trạng thái `reserved`/`occupied` chỉ do hợp đồng đặt. Chủ trọ chỉ tự đổi qua lại `vacant` ↔ `maintenance`
  (`409 rental.room_status_locked` nếu phòng đang có hợp đồng).

**US-1003 (M) Tài khoản nhận tiền** — chọn ngân hàng từ danh mục `banks`, số tài khoản 6–19 chữ số, tên chủ tài khoản
(chữ in hoa không dấu, như trên app ngân hàng).
- AC1: API chỉ trả gợi ý `•••1234`; không endpoint nào trả lại số đầy đủ, kể cả cho chủ trọ. Số đầy đủ chỉ được giải
  mã khi sinh chuỗi VietQR.
- AC2: Lưu hoặc đổi số → kiểm BR-31. Bị gắn cờ thì chủ trọ thấy "Tài khoản nhận tiền đang chờ cán bộ xem xét".
- AC3: Đổi số tài khoản không ảnh hưởng hóa đơn đã phát hành: mỗi hóa đơn chụp lại tài khoản lúc phát hành.

**US-1004 (S) Cán bộ xem xét tài khoản bị gắn cờ** — xem hồ sơ chủ, số báo cáo đã xác nhận của định danh (không xem
số đầy đủ, chỉ gợi ý), rồi chọn *Gỡ cờ* hoặc *Giữ cờ* kèm lý do. Lần xem xét gần nhất (người xem xét, thời điểm, lý do) lưu ngay trên
`payout_accounts`; lịch sử đầy đủ các lần xem xét thuộc `audit_events` của Giai đoạn 3. Chủ trọ lưu lại **đúng số cũ**
thì giữ nguyên trạng thái và lần xem xét; đổi sang số khác thì xóa lần xem xét và kiểm lại BR-31.

**US-1005 (M) Sinh tin từ phòng trống** — điền sẵn tiêu đề, giá, diện tích, khép kín, tiện nghi, địa chỉ, vị trí từ
phòng và nhà. Chủ trọ bổ sung mô tả, ảnh rồi gửi duyệt như US-304 (BR-32).

### E11 — Hợp đồng thuê

**US-1101 (M) Soạn hợp đồng** — phòng, email người thuê (1–4 người), ngày bắt đầu, ngày kết thúc, tiền thuê, tiền cọc,
ngày chốt hóa đơn, đơn giá điện (đ/kWh), đơn giá nước (đ/m³ hoặc đ/người), các khoản dịch vụ cố định (tên + số tiền),
điều khoản thêm (≤ 3.000 ký tự). Lưu ở `draft`, sửa được kèm `version`.

**US-1102 (M) Gửi hợp đồng**
- AC1: Không đạt BR-21 → `422 rental.landlord_not_ready` kèm danh sách việc còn thiếu (như `posting.missing`).
- AC2: Thành công: chốt `content_sha256`, hợp đồng sang `awaiting_signatures`, phòng sang `reserved`, mỗi email bên thuê
  nhận một link mời hạn 7 ngày. Phản hồi giống nhau dù email đã có tài khoản hay chưa (BR-27).
- AC3: Phòng đã có hợp đồng chồng thời gian → `409 rental.room_already_leased`.

**US-1103 (M) Nhận lời mời** — người nhận mở link, đăng nhập hoặc đăng ký bằng đúng email được mời rồi bấm *Nhận*.
- AC1: Đăng nhập bằng email khác → `404` (không lộ lời mời là của ai).
- AC2: Link hết hạn hoặc hợp đồng đã hủy → `410 rental.invite_expired`.
- AC3: Tài khoản chưa có `rental.tenant` (ví dụ chỉ là `owner`) → hướng dẫn thêm vai trò người thuê. MVP: quản trị viên
  gán vai trò `renter`.

**US-1104 (M) Xác nhận hợp đồng (mọi bên)** — xem toàn văn hợp đồng, bấm *Gửi mã*, nhập OTP (BR-25).
- AC1: Đủ xác nhận → `active`, phòng `occupied`, tin của phòng (nếu có) về `draft` (BR-32). Mọi bên nhận email xác nhận
  kèm mã băm.
- AC2: Trang hợp đồng có bản in (CSS in), hiện mã băm, thời điểm và email của từng bên xác nhận, và dòng "Hợp đồng xác
  nhận điện tử bằng mã OTP qua email, không phải chữ ký số".

**US-1105 (M) Hủy, kết thúc hợp đồng** — chủ phòng hủy khi `draft`/`awaiting_signatures`; kết thúc sớm khi `active`
(BR-33). Job đêm tự kết thúc khi qua `end_date`, và gửi email nhắc mọi bên 30 ngày trước ngày kết thúc.

### E12 — Hóa đơn và thanh toán

**US-1201 (M) Lập hóa đơn tháng** — chọn hợp đồng `active` và kỳ `yyyy-MM`. API điền sẵn dòng tiền phòng, dòng dịch vụ
cố định, chỉ số điện/nước đầu kỳ (BR-29). Chủ trọ nhập chỉ số cuối kỳ, thêm dòng khác nếu cần (ví dụ "Sửa vòi nước").
Hóa đơn lưu ở `draft`.

**US-1202 (M) Phát hành hóa đơn**
- AC1: Kiểm BR-21 và BR-28, chụp lại tài khoản nhận tiền, sinh `transfer_code` duy nhất, hạn thanh toán = ngày phát hành
  + 7 ngày (chỉnh được 1–30). Bên thuê nhận email.
- AC2: Tài khoản đang `flagged` → `409 rental.payout_account_flagged`.

**US-1203 (M) Bên thuê xem và trả** — xem các dòng, tổng tiền, hạn thanh toán, mã VietQR (số tiền và nội dung
chuyển khoản điền sẵn), và nút chép số tài khoản/nội dung chuyển khoản.
- AC1: Tài khoản bị gắn cờ sau khi phát hành → ẩn mã QR, hiện "Số tài khoản nhận tiền này có báo cáo đã được xác nhận.
  Hãy liên hệ chủ trọ và kiểm tra kỹ trước khi chuyển tiền." Câu này không có chữ "lừa đảo" (quy tắc 17).
- AC2: Lời khuyên cố định dưới mã QR: "Chỉ chuyển đúng số tiền, đúng nội dung. Hệ thống không giữ tiền và không bao giờ
  yêu cầu bạn chuyển vào tài khoản khác."

**US-1204 (M) Ghi nhận đã thu** — chủ phòng ghi số tiền, ngày thu, hình thức (`bank_transfer` / `cash`), ghi chú. Thu
nhiều lần được; vượt tổng → `422 rental.payment_exceeds_total`. Đủ tổng thì hóa đơn sang `paid`; bên thuê thấy
"Chủ trọ đã xác nhận nhận đủ". Ghi sai thì chủ phòng *Đảo* lần thu đó (tạo dòng đảo, kèm ghi chú); bên thuê thấy cả
hai dòng.

**US-1205 (M) Hủy hóa đơn** — chủ phòng hủy hóa đơn `issued` có số đã thu ròng bằng 0, kèm lý do. Còn tiền đã thu thì
phải đảo các lần thu trước (`409 rental.invoice_has_payments`).

**US-1206 (S) Nhắc quá hạn** — job đêm gửi email cho bên thuê 1 ngày sau hạn thanh toán, nhắc tối đa 3 lần, cách nhau
3 ngày.

## Cơ sở dữ liệu

Migration `0005_rental_core.sql`. Mọi bảng nằm trong schema `app` và theo đúng quy ước ở 03-database.md: tên ràng buộc,
một nghĩa cho mỗi NULL, tiền là `INTEGER` VND, `public_id` UUID cho tài nguyên hai bên cùng thấy. Sau migration này
CSDL có 42 bảng.

### Kiểu và dữ liệu tham chiếu

```sql
CREATE TYPE app.room_status           AS ENUM ('vacant', 'reserved', 'occupied', 'maintenance');
CREATE TYPE app.lease_status          AS ENUM ('draft', 'awaiting_signatures', 'active', 'ended', 'cancelled');
CREATE TYPE app.lease_party_role      AS ENUM ('landlord', 'tenant');
CREATE TYPE app.invoice_status        AS ENUM ('draft', 'issued', 'paid', 'void');
CREATE TYPE app.invoice_line_kind     AS ENUM ('rent', 'electricity', 'water', 'service', 'other');
CREATE TYPE app.payment_method        AS ENUM ('bank_transfer', 'cash');
CREATE TYPE app.payout_account_status AS ENUM ('active', 'flagged');
```

Migration seed thêm:

- vai trò `renter` và hai quyền `rental.manage`, `rental.tenant`, gán theo bảng Phân quyền;
- danh mục `banks` gồm các ngân hàng phổ biến theo mã BIN của NAPAS: Vietcombank 970436, VietinBank 970415,
  BIDV 970418, Agribank 970405, MB 970422, Techcombank 970407, ACB 970416, VPBank 970432, TPBank 970423,
  Sacombank 970403. **Đối chiếu lại với danh sách NAPAS hiện hành khi viết migration.**

### Bảng

| Bảng | Cột chính | Ràng buộc đáng chú ý |
|---|---|---|
| `banks` | `id SMALLINT`, `bin CHAR(6)`, `short_name`, `name`, `is_active` | `uq_banks_bin`; `ck_banks_bin` (6 chữ số) |
| `payout_accounts` | `owner_id` (PK, 1–1 với `users`), `bank_id`, `account_ciphertext BYTEA`, `key_id`, `account_hmac BYTEA`, `account_hint`, `holder_name`, `status`, `reviewed_by`, `reviewed_at`, `review_note`, `updated_at` | `ck_payout_accounts_hmac_len` (32 byte); `ck_payout_accounts_holder` (chữ in hoa A–Z và dấu cách, ≤ 50); `reviewed_*` cùng NULL hoặc cùng có giá trị |
| `properties` | `id`, `owner_id`, `name`, `ward_code`, `street_address`, `location GEOGRAPHY(Point,4326)`, `note`, `created_at` | `fk` tới `administrative_units` (cấp xã); `ix_properties_owner_id` |
| `rooms` | `id`, `property_id`, `code`, `floor`, `area_m2 NUMERIC(5,1)`, `is_self_contained`, `list_rent_vnd`, `status`, `version`, `created_at` | `uq_rooms_property_code`; `ck_rooms_list_rent` (100.000–100.000.000) |
| `rental_room_amenities` | `room_id`, `amenity_id` | PK kép; dùng chung danh mục `amenities` |
| `leases` | `id`, `public_id UUID`, `room_id`, `status`, `start_date`, `end_date`, `rent_vnd`, `deposit_vnd`, `billing_day`, `electricity_price_vnd`, `water_price_vnd`, `water_unit` (`m3`/`person`), `services JSONB`, `extra_terms`, `content_sha256 BYTEA`, `sent_at`, `invite_expires_at`, `activated_at`, `ended_at`, `cancelled_at`, `version`, `created_at` | `ex_leases_room_period`: `EXCLUDE USING gist (room_id WITH =, daterange(start_date, end_date, '[)') WITH &&) WHERE (status IN ('awaiting_signatures','active'))` (cần `btree_gist`: trusted extension nên `0005` tự tạo trong schema `app`, không cần sửa `00_init.sh`); `ck_leases_dates`; `ck_leases_duration` (1–36 tháng); `ck_leases_billing_day` (1–28); `ck_leases_sent` (`content_sha256`, `sent_at`, `invite_expires_at` NULL ⇔ `status = 'draft'`) |
| `lease_parties` | `id`, `lease_id`, `role`, `user_id` (NULL: bên thuê chưa nhận lời mời), `invite_email`, `invite_token_hash BYTEA`, `claimed_at`, `confirmed_at`, `display_name` | `uq_lease_parties_lease_user`; `uq_lease_parties_lease_email`; đúng một `landlord` mỗi hợp đồng (UNIQUE một phần); trigger `check_lease_party`: landlord = chủ nhà của phòng, tenant ≠ landlord, tối đa 4 tenant |
| `invoices` | `id`, `public_id UUID`, `lease_id`, `period CHAR(7)`, `status`, `total_vnd`, `due_date`, `transfer_code`, `bank_id`, `account_ciphertext`, `key_id`, `account_hint`, `holder_name` (bản chụp lúc phát hành), `issued_at`, `paid_at`, `voided_at`, `void_reason`, `version`, `created_at` | `uq_invoices_lease_period` WHERE `status <> 'void'`; `uq_invoices_transfer_code`; `ck_invoices_period` (`^\d{4}-(0[1-9]|1[0-2])$`); `ck_invoices_issued` (các cột chụp và `issued_at` NULL ⇔ `draft`); `ck_invoices_void` |
| `invoice_lines` | `id`, `invoice_id`, `kind`, `label`, `meter_start NUMERIC(10,1)`, `meter_end NUMERIC(10,1)`, `quantity NUMERIC(10,1)`, `unit_price_vnd`, `amount_vnd`, `sort_order` | `ck_invoice_lines_meter` (`meter_end ≥ meter_start`, cả hai NULL với dòng không phải điện/nước); `ck_invoice_lines_amount` (≥ 0) |
| `payments` | `id`, `invoice_id`, `amount_vnd`, `paid_on DATE`, `method`, `note`, `reverses_payment_id` (NULL: lần thu thường; khác NULL: dòng đảo một lần thu ghi sai), `recorded_by`, `recorded_at` | `ck_payments_amount` (> 0); `uq_payments_reverses_payment_id` (mỗi lần thu đảo tối đa một lần); dòng đảo có `amount_vnd` bằng dòng gốc và cùng hóa đơn. **Chỉ thêm**: không UPDATE/DELETE, sửa sai bằng dòng đảo |

Sửa bảng cũ: `room_listings` thêm `room_id BIGINT NULL` (NULL: tin đăng tay, không gắn phòng) +
`uq_room_listings_room_id` WHERE `room_id IS NOT NULL`. UNIQUE một phần này chưa đủ để chặn "tin chưa removed": API
kiểm BR-32 trong transaction có `SELECT … FOR UPDATE` trên phòng.

Trigger:

- `check_invoice_total` (DEFERRABLE INITIALLY DEFERRED, khi hóa đơn rời `draft`): `total_vnd = SUM(amount_vnd)`.
- `check_payment_total`: số đã thu ròng (lần thu thường trừ dòng đảo) nằm trong `[0, total_vnd]`, và hóa đơn đang `issued` hoặc `paid`. Ròng bằng `total_vnd` thì API chuyển hóa đơn sang `paid`; dòng đảo làm ròng nhỏ hơn tổng thì hóa đơn về `issued`.

### Quyền theo role

| Role | Quyền |
|---|---|
| `app_api` | `SELECT, INSERT, UPDATE` trên bảng mới (riêng `payments` chỉ `SELECT, INSERT`); `DELETE` trên `rental_room_amenities` (sửa tiện nghi phòng) và `invoice_lines` (API chỉ xóa khi hóa đơn còn `draft`); `SELECT` trên `banks` |
| `app_maintenance` | `SELECT` + `UPDATE (status, ended_at, cancelled_at)` trên `leases`; `UPDATE (status)` trên `rooms`; `DELETE` hợp đồng, hóa đơn quá hạn lưu giữ (BR-34, xóa theo thứ tự con → cha); `SELECT` các bảng cần cho email nhắc |
| `app_worker`, `app_reporting` | Không có quyền gì trên các bảng mới ở GĐ 1 |

### Ánh xạ lỗi CSDL → mã lỗi

| Ràng buộc | Mã lỗi |
|---|---|
| `ex_leases_room_period` | `409 rental.room_already_leased` |
| `uq_rooms_property_code` | `409 rental.room_code_taken` |
| `uq_invoices_lease_period` | `409 rental.invoice_period_exists` |
| `uq_lease_parties_lease_email` | `422 validation.failed` (email trùng trong một hợp đồng) |
| trigger `check_lease_party` | `422 rental.invalid_party` |
| trigger `check_payment_total` | `422 rental.payment_exceeds_total` |

### Mã băm hợp đồng

`content_sha256` = SHA-256 của chuỗi UTF-8 JSON có khóa sắp theo thứ tự chữ cái, không có khoảng trắng thừa, ngày dạng
`yyyy-MM-dd`, tiền là số nguyên. Nội dung băm gồm:

- `leaseId`;
- thông tin phòng: tên nhà, địa chỉ, mã phòng;
- email của các bên;
- mọi điều khoản tiền và ngày;
- `services`, `extraTerms`;
- phiên bản mẫu hợp đồng (`templateVersion`, bắt đầu `1`).

Hàm chuẩn hóa có test vector cố định; trang in hiện mã băm dạng hex để hai bên đối chiếu.

## API

Tiền tố `/api/v1`. Quy ước chung, định dạng lỗi và cách phân trang như 04-api.md. Hợp đồng và hóa đơn dùng `public_id`
(`leaseId`, `invoiceId`). Nhà và phòng dùng `id` số, luôn kèm kiểm tra quyền sở hữu.

### Chủ trọ

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/me/payout-account` | `rental.manage` | `{bankId, accountHint, holderName, status}` hoặc `404` khi chưa có |
| PUT | `/me/payout-account` | `rental.manage` | `{bankId, accountNumber, holderName}` → `200 {accountHint, status}` (BR-31) |
| GET, POST | `/rental/properties` | `rental.manage` | Danh sách / tạo nhà |
| GET, PUT | `/rental/properties/{id}` | chủ nhà | Chi tiết (kèm danh sách phòng) / sửa |
| POST | `/rental/properties/{id}/rooms` | chủ nhà | Tạo phòng → `201 {id, version}` |
| PUT | `/rental/rooms/{id}` | chủ nhà | Sửa phòng, kèm `version`; đổi `vacant` ↔ `maintenance` |
| POST | `/rental/rooms/{id}/listing` | chủ nhà + `listing.create` | Sinh tin nháp → `201 {listingId, version}` (BR-32) |
| GET | `/rental/leases?status=&roomId=` | `rental.manage` | Hợp đồng của các phòng mình |
| POST | `/rental/leases` | `rental.manage` | Soạn nháp → `201 {leaseId, version}` |
| GET, PUT | `/rental/leases/{leaseId}` | chủ nhà | Chi tiết / sửa nháp (kèm `version`) |
| POST | `/rental/leases/{leaseId}/send` | chủ nhà | `{version}` → `202 {status: "awaiting_signatures", contentSha256}` (BR-21, BR-24, BR-27) |
| POST | `/rental/leases/{leaseId}/cancel` | chủ nhà | `{version}` → `204` (chỉ `draft`, `awaiting_signatures`) |
| POST | `/rental/leases/{leaseId}/end` | chủ nhà | `{version, endDate}` → `204` (BR-33) |
| POST | `/rental/leases/{leaseId}/invoices` | chủ nhà | `{period, electricityEnd?, waterEnd?, extraLines: [{label, amountVnd}]}` → `201 {invoiceId, version}` |
| GET | `/rental/invoices?leaseId=&status=&period=` | `rental.manage` | Hóa đơn của mình |
| GET, PUT | `/rental/invoices/{invoiceId}` | chủ nhà | Chi tiết / sửa nháp |
| POST | `/rental/invoices/{invoiceId}/issue` | chủ nhà | `{version, dueInDays?}` → `200 {transferCode, dueDate}` |
| POST | `/rental/invoices/{invoiceId}/void` | chủ nhà | `{version, reason}` → `204` |
| POST | `/rental/invoices/{invoiceId}/payments` | chủ nhà | `{amountVnd, paidOn, method, note?}` → `201 {paymentId, invoiceStatus}` |
| POST | `/rental/invoices/{invoiceId}/payments/{paymentId}/reverse` | chủ nhà | `{note}` → `201 {invoiceStatus}`; lần thu đã đảo → `409 rental.payment_already_reversed` |

### Mọi bên trong hợp đồng

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| POST | `/leases/{leaseId}/otp` | bên của hợp đồng (chủ phòng hoặc bên thuê đã nhận lời mời) | Gửi OTP tới email của người gọi → `204`. Hạn mức 3/giờ/người/hợp đồng |
| POST | `/leases/{leaseId}/confirm` | như trên | `{code, contentSha256}` → `200 {status}` (BR-25) |

### Người thuê

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/tenancy/invites/{token}` | đăng nhập | Xem tóm tắt lời mời. Email đăng nhập khác email được mời → `404`; hết hạn → `410 rental.invite_expired` |
| POST | `/tenancy/invites/{token}/claim` | `rental.tenant` | Gắn `user_id` vào bên thuê → `200 {leaseId}` |
| GET | `/tenancy/leases` | `rental.tenant` | Hợp đồng mình là bên thuê |
| GET | `/tenancy/leases/{leaseId}` | bên thuê | Toàn văn, mã băm, các bên và thời điểm xác nhận |
| GET | `/tenancy/invoices?status=` | `rental.tenant` | Hóa đơn của các hợp đồng mình là bên thuê (chỉ `issued`, `paid`, `void`) |
| GET | `/tenancy/invoices/{invoiceId}` | bên thuê | Dòng, tổng, hạn, `transferCode`, `bankShortName`, `accountHint`, `holderName`, `qrAvailable`, `warning` |
| GET | `/tenancy/invoices/{invoiceId}/qr.svg` | bên thuê | `image/svg+xml`, `Cache-Control: no-store`; `404` khi `qrAvailable = false` |

Số tài khoản đầy đủ chỉ nằm **bên trong** ảnh QR. Ở giao diện, nút "Chép số tài khoản" gọi
`GET /tenancy/invoices/{invoiceId}/account` (`{accountNumber}`, `no-store`). Endpoint này chỉ phục vụ bên thuê của hóa
đơn `issued` và mỗi lần gọi ghi log id hóa đơn (không log số tài khoản).

### Cán bộ, danh mục

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/moderation/payout-accounts?status=flagged` | `owner.verify` | Danh sách chủ có tài khoản bị gắn cờ: hồ sơ chủ, `accountHint`, số báo cáo đã xác nhận |
| POST | `/moderation/payout-accounts/{ownerId}/review` | `owner.verify` | `{decision: "clear" \| "keep", note}` → `204`; người xem xét không phải chính chủ (BR-19) |
| GET | `/meta/banks` | khách | Ngân hàng đang dùng: `{id, bin, shortName, name}` |

### Mã lỗi mới

| Mã | HTTP | Khi nào | Thông báo gợi ý |
|---|---|---|---|
| `rental.landlord_not_ready` | 422 | Gửi hợp đồng, phát hành hóa đơn khi chưa đạt BR-21 | Bạn cần hoàn tất các bước sau trước |
| `rental.room_already_leased` | 409 | Hợp đồng chồng thời gian | Phòng đã có hợp đồng trong thời gian này |
| `rental.room_status_locked` | 409 | Tự đổi trạng thái phòng đang có hợp đồng | Trạng thái phòng do hợp đồng quyết định |
| `rental.room_code_taken` | 409 | Trùng mã phòng trong nhà | Mã phòng đã có trong nhà này |
| `rental.room_not_vacant` | 409 | Sinh tin khi phòng không trống, hoặc phòng đã có tin | Chỉ đăng tin cho phòng đang trống |
| `rental.lease_not_editable` | 409 | Sửa hợp đồng đã gửi | Hợp đồng đã gửi, hãy hủy và soạn lại |
| `rental.lease_content_changed` | 409 | `contentSha256` lệch | Nội dung hợp đồng đã thay đổi, tải lại để xem |
| `rental.lease_already_confirmed` | 409 | Xác nhận lần hai | Bạn đã xác nhận hợp đồng này |
| `rental.invite_expired` | 410 | Lời mời hết hạn / hợp đồng đã hủy | Lời mời đã hết hạn |
| `rental.invalid_party` | 422 | Vi phạm BR-23 | Thông tin các bên không hợp lệ |
| `rental.invoice_period_exists` | 409 | Trùng kỳ | Kỳ này đã có hóa đơn |
| `rental.invoice_not_editable` | 409 | Sửa hóa đơn đã phát hành | Hóa đơn đã phát hành, hãy hủy và lập lại |
| `rental.invoice_has_payments` | 409 | Hủy hóa đơn đã có lần thu | Hóa đơn đã ghi nhận thu tiền |
| `rental.meter_decreased` | 422 | Chỉ số cuối < đầu kỳ (BR-29) | Chỉ số cuối kỳ nhỏ hơn đầu kỳ |
| `rental.payment_exceeds_total` | 422 | Tổng thu vượt hóa đơn | Số tiền vượt quá số còn phải thu |
| `rental.payment_already_reversed` | 409 | Đảo một lần thu hai lần | Lần thu này đã được đảo |
| `rental.payout_account_missing` | 422 | Phát hành khi chưa có tài khoản nhận tiền | Thêm tài khoản nhận tiền trước |
| `rental.payout_account_flagged` | 409 | Phát hành khi tài khoản bị gắn cờ | Tài khoản nhận tiền đang chờ cán bộ xem xét |
| `rental.invoice_total_mismatch` | 422 | Tổng dòng ≠ `total_vnd` lúc phát hành (trigger, lỗi lập trình nếu lọt tới client) | Tổng hóa đơn không khớp các dòng |
| `rental.invoice_not_payable` | 409 | Ghi thu trên hóa đơn `draft`/`void` | Hóa đơn chưa phát hành hoặc đã hủy |
| `rental.invalid_reversal` | 422 | Dòng đảo khác hóa đơn, khác số tiền, hoặc đảo một dòng đảo | Không đảo được lần thu này |
| `rental.version_conflict` | 409 | `version` lệch (phòng, hợp đồng, hóa đơn) | Dữ liệu vừa được cập nhật ở nơi khác |
| `me.active_lease` | 409 | Chủ trọ xóa tài khoản khi còn hợp đồng (BR-34) | Hãy kết thúc hoặc hủy hợp đồng trước |

### Hạn mức thêm

| Phạm vi | Giới hạn |
|---|---|
| `POST /rental/leases/{id}/send` | 20/ngày/tài khoản (mỗi lần gửi tạo email mời) |
| `POST /leases/{id}/otp` | 3/giờ/người/hợp đồng; 10/ngày/người |
| `GET /tenancy/invoices/{id}/account` | 20/giờ/tài khoản |

### VietQR

API sinh chuỗi theo chuẩn EMVCo của NAPAS. Sau đó QRCoder vẽ chuỗi thành SVG, mức sửa lỗi M. Chuỗi gồm:

- `00` phiên bản `01`;
- `01` = `12` (QR động, có số tiền);
- `38` thông tin người nhận: GUID `A000000727`, BIN ngân hàng, số tài khoản, dịch vụ `QRIBFTTA`;
- `53` = `704` (VND), `54` số tiền, `58` = `VN`;
- `62.08` nội dung chuyển khoản = `transfer_code`;
- `63` CRC16-CCITT-FALSE.

Test: so chuỗi sinh ra với test vector cố định, và test CRC với giá trị đã biết. `transfer_code` có dạng `TT` + 8 ký tự
Crockford base32 viết hoa (không có ký tự dễ nhầm), duy nhất toàn hệ thống. Mã này chỉ là nội dung chuyển khoản, không
mang thông tin cá nhân.

## Giao diện

Chữ giao diện tiếng Việt; tiền dạng `2.500.000 ₫`; ngày `dd/MM/yyyy` theo giờ Việt Nam. Trang bị chặn theo quyền.

| Đường dẫn | Ai vào được | Nội dung | API |
|---|---|---|---|
| `/chu-tro` | `rental.manage` | Tổng quan: số phòng theo trạng thái, hóa đơn chưa thu, hợp đồng sắp hết hạn; việc còn thiếu (BR-21) | `/rental/*` |
| `/chu-tro/nha/moi`, `/chu-tro/nha/[id]` | `rental.manage` | Form nhà (ghim bản đồ như form tin), danh sách phòng, nút *Đăng tin phòng này* | `/rental/properties*`, `/rental/rooms*` |
| `/chu-tro/hop-dong`, `/chu-tro/hop-dong/moi`, `/chu-tro/hop-dong/[leaseId]` | `rental.manage` | Danh sách, form soạn, chi tiết + gửi, xác nhận OTP, hủy, kết thúc | `/rental/leases*`, `/leases/*` |
| `/chu-tro/hoa-don`, `/chu-tro/hoa-don/[invoiceId]` | `rental.manage` | Lập theo kỳ (chỉ số đầu kỳ điền sẵn), phát hành, ghi nhận thu, hủy | `/rental/invoices*` |
| `/toi/tai-khoan-nhan-tien` | `rental.manage` | Chọn ngân hàng, nhập số, trạng thái gắn cờ | `/me/payout-account`, `/meta/banks` |
| `/loi-moi/[token]` | đăng nhập | Tóm tắt lời mời → *Nhận* → chuyển sang trang hợp đồng. Chưa đăng nhập thì chuyển tới đăng nhập/đăng ký, có sẵn email | `/tenancy/invites/*` |
| `/toi/thue` | `rental.tenant` | Hợp đồng và hóa đơn của tôi | `/tenancy/*` |
| `/toi/thue/hop-dong/[leaseId]` | bên thuê | Toàn văn, mã băm, xác nhận OTP, bản in | `/tenancy/leases/*`, `/leases/*` |
| `/toi/thue/hoa-don/[invoiceId]` | bên thuê | Dòng, tổng, hạn, mã QR hoặc cảnh báo, chép số tài khoản/nội dung | `/tenancy/invoices/*` |
| `/kiem-duyet/tai-khoan-nhan-tien` | `owner.verify` | Danh sách bị gắn cờ, xem xét | `/moderation/payout-accounts*` |

Trang đăng ký thêm lựa chọn **Người thuê** bên cạnh Sinh viên và Chủ trọ / nhà tuyển dụng.

Trang hợp đồng có stylesheet `@media print`: ẩn thanh điều hướng, in toàn văn, mã băm, các bên và thời điểm xác nhận.
Người dùng dùng chức năng in của trình duyệt (in ra giấy hoặc lưu PDF). Máy chủ không tạo PDF.

Service worker không cache `/api/v1/tenancy/*`, `/api/v1/rental/*` và `/api/v1/leases/*`, như với dữ liệu cá nhân khác.

## Bảo mật và riêng tư

- **Số tài khoản nhận tiền:**
  - mã hóa AES-256-GCM bằng bộ khóa giấy tờ sẵn có (`Crypto__DocumentKeys`, ghi `key_id`), với AAD
    `payout-account:{ownerId}`; bản chụp trên hóa đơn dùng AAD `invoice:{invoiceId}`. Gắn ngữ cảnh như vậy thì bản mã
    chép sang dòng khác sẽ không giải mã được;
  - HMAC dùng `Crypto__IdentifierHmacKey` và chuẩn hóa giống `bank_account` ở 05-ai-worker.md (chỉ chữ số), nhờ đó so
    khớp được với `identifiers`;
  - không thêm biến môi trường mới.
- **Lời mời:** token 256 bit ngẫu nhiên; CSDL chỉ lưu SHA-256 của token. Link dạng `/loi-moi/{token}`. Không log token,
  email hay số tài khoản (quy tắc 13); chỉ log `leaseId`, `invoiceId`, `userId`.
- **OTP hợp đồng:** lưu trong Redis như OTP số điện thoại (`otp:lease:{leaseId}:{userId}`), so sánh hằng thời gian.
  Mã gửi qua email nên mức tin cậy bằng việc kiểm soát hộp thư; điều này ghi trong hợp đồng (US-1104 AC2).
- **IDOR:** mọi endpoint `/rental/*` và `/tenancy/*` có test truy cập chéo giữa hai chủ trọ và giữa hai người thuê →
  `404`.
- **Không cầm tiền:** không endpoint nào nhận thông tin thẻ, không gọi API ngân hàng, không giữ số dư. Ghi nhận thanh toán
  chỉ là dữ liệu do chủ trọ khai.
- **Hồ sơ đánh giá tác động** (07-security.md) bổ sung: số tài khoản của chủ trọ, email của người thuê, nội dung hợp đồng
  và hóa đơn; thời hạn lưu theo BR-34.
- **Hạn chế đã biết:** OTP email yếu hơn chữ ký số. Chủ trọ có thể ghi nhận thu sai, nhưng không có lợi gì, và bên thuê
  thấy ngay trên hóa đơn.

## Kiểm thử

| Loại | Ca cần có |
|---|---|
| CSDL (Testcontainers) | `ex_leases_room_period` chặn hợp đồng chồng ngày, cho phép nối tiếp `[)`; hợp đồng `cancelled` không chặn; `check_lease_party` (landlord ≠ chủ phòng, tenant = landlord, tenant thứ 5); `check_invoice_total`; `check_payment_total` (vượt tổng, hóa đơn `void`, dòng đảo đưa `paid` về `issued`, đảo hai lần); `uq_invoices_lease_period` cho phép lập lại sau khi `void`; `app_api` không UPDATE/DELETE được `payments`; `app_maintenance` đủ quyền cho job đêm |
| API | Mỗi AC của US-1001…1206; IDOR chéo chủ trọ / người thuê; lời mời không lộ email đã có tài khoản (cùng mã trạng thái, cùng thân phản hồi); OTP sai 5 lần; `contentSha256` lệch; BR-21 trả đủ danh sách việc thiếu; tài khoản bị gắn cờ ẩn QR và chặn phát hành; tin `verified` về `draft` khi hợp đồng `active` (có dòng `listing_transitions`); `me.active_lease` |
| Đơn vị | Mã băm hợp đồng (test vector); chuỗi VietQR + CRC16 (test vector); `transfer_code` đúng định dạng; tính tiền điện/nước từ chỉ số |
| Job đêm | Hủy hợp đồng quá hạn mời, kết thúc hợp đồng quá `end_date` (phòng về `vacant`), email nhắc 30 ngày, nhắc quá hạn tối đa 3 lần, xóa dữ liệu quá 2 năm |
| Đầu cuối (Playwright) | Chủ trọ tạo nhà, phòng, tài khoản nhận tiền → soạn và gửi hợp đồng → người thuê nhận lời mời qua Mailpit, xác nhận OTP → chủ trọ xác nhận → lập, phát hành hóa đơn → người thuê thấy mã QR → chủ trọ ghi nhận thu đủ |

## Task

Đánh số tiếp kế hoạch (10-implementation-plan.md, Giai đoạn 6). Mỗi task một nhánh, có test. Prompt theo mẫu chung của
10-implementation-plan.md: đọc AGENTS.md và các mục của file này được liệt kê, lập kế hoạch trước khi code.

| Task | Phụ thuộc | Nội dung | Xong khi |
|---|---|---|---|
| T6.1 Lược đồ thuê trọ | Bảo vệ xong | Migration `0005_rental_core.sql` (kiểu, bảng, trigger, GRANT, seed vai trò, quyền, ngân hàng, `room_listings.room_id`); cập nhật 03-database.md (danh mục bảng, ánh xạ lỗi) | Migration chạy trên CSDL rỗng; test CSDL ở mục Kiểm thử xanh |
| T6.2 Nhà, phòng, tài khoản nhận tiền | T6.1 | `Features/Rental` (nhà, phòng), `/me/payout-account` (mã hóa + HMAC + gắn cờ), `/moderation/payout-accounts*`, `/meta/banks`, `/rental/rooms/{id}/listing`; đăng ký `accountType: "renter"` | AC US-1001…1005; IDOR |
| T6.3 Hợp đồng | T6.2 | Soạn, gửi, mời, nhận lời mời, OTP, xác nhận, hủy, kết thúc; mã băm; email qua outbox; đưa tin về `draft` khi `active`; job đêm (hủy quá hạn mời, kết thúc, nhắc 30 ngày); `me.active_lease` | AC E11; test lời mời không lộ email |
| T6.4 Hóa đơn và VietQR | T6.3 | Lập, phát hành, hủy, ghi nhận thu; VietQR + QRCoder; `/tenancy/invoices*`; nhắc quá hạn; xóa dữ liệu quá hạn lưu (BR-34) | AC E12; test vector VietQR |
| T6.5 Web | T6.2–T6.4 | Các trang ở mục Giao diện; `make gen-api` | `pnpm lint`, `typecheck`, `test`, `build` sạch |
| T6.6 Đầu cuối và rà soát | T6.5 | Kịch bản Playwright; bổ sung 07-security.md (hồ sơ đánh giá tác động), `docs/security-review.md` | Nightly xanh 3 lần liên tiếp |
