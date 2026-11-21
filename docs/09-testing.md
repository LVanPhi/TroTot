# 09 — Kiểm thử

Mỗi task chỉ xong khi có test cho các tiêu chí nghiệm thu nó phụ trách (01-requirements.md) và CI xanh.
**Không** xóa, bỏ qua hay nới test để cho qua; test sai thì sửa test kèm giải thích trong PR.

## Công cụ và độ phủ

| Tầng | Công cụ | Mục tiêu |
|---|---|---|
| CSDL | Testcontainers (image `infra/postgres`), chạy migration thật | 100% danh sách ca ở mục Kiểm thử CSDL |
| API — đơn vị | xUnit v3 + Shouldly | ≥ 80% dòng cho `Domain/`, service nghiệp vụ, validator |
| API — tích hợp | `WebApplicationFactory` + Testcontainers (PostgreSQL, Redis) + S3 giả lập trong bộ nhớ | Mọi endpoint có ít nhất một test thành công và một test lỗi |
| Worker | pytest; testcontainers-python cho PostgreSQL/Redis; LLM giả (`llm/fake.py`) | ≥ 80% dòng cho `pipeline/` |
| Web — thành phần | Vitest + Testing Library | Thành phần domain, form, định dạng |
| Đầu cuối | Playwright trên toàn bộ stack Docker | 4 luồng chính (mục E2E) |
| Bảo mật | OWASP ZAP baseline (workflow `nightly.yml` trên stack Docker đầy đủ), test ma trận phân quyền | Không có cảnh báo mức High |
| Tải | k6 | Ngưỡng ở mục Kiểm thử tải |
| AI | Script E1–E5 (05-ai-worker.md) | Chỉ tiêu ở 00-overview.md |

## Kiểm thử CSDL

Các ca D1–D22 đã chạy thử khi viết lược đồ (PostgreSQL 16); 11/2026 chạy lại `0001`–`0003` trên PostgreSQL 18.7
và kiểm tay D2, D6, D7, D21, D22, D23–D25. Chuyển tất cả thành test tự động (`TroViec.Api.Tests/Database/`)
chạy bằng đúng role tương ứng:

| # | Ca | Role | Kết quả mong đợi |
|---|---|---|---|
| D1 | Thêm `job_listings` cho một tin phòng | app_api | Lỗi FK `fk_job_listings_listing` |
| D2 | Ghi chuyển trạng thái `draft → verified` | app_api | Lỗi CHECK `ck_listing_transitions_allowed` |
| D3 | `pending_review → rejected` không có lý do | app_api | Lỗi CHECK `ck_listing_transitions_reason` |
| D4 | Đặt `verified` khi `expires_at` NULL | app_api | Lỗi CHECK `ck_listings_verified_has_expiry` |
| D5 | Ghi trực tiếp `identifier_stats` | app_api | `permission denied` |
| D6 | UPDATE `listing_transitions` | app_api | `permission denied` |
| D7 | Đọc `users.email` | app_worker | `permission denied`; đọc `users.created_at` thì được |
| D8 | UPDATE `scans.input_text` | app_worker | `permission denied` (chỉ được cột `status`, `completed_at`, `error_code`) |
| D9 | Tiêu đề chỉ có khoảng trắng | app_api | Lỗi CHECK `ck_listings_title_not_blank` |
| D10 | Số điện thoại `0912345678` (chưa chuẩn hóa) | app_api | Lỗi CHECK `ck_users_phone_format` |
| D11 | Lượt `quick_check` có `listing_id` | app_api | Lỗi CHECK `ck_scans_source_consistency` |
| D12 | Ghi điểm hai lần cùng `(scan_id, aggregator_model_id)` với `ON CONFLICT DO NOTHING` | app_worker | Chỉ còn một dòng |
| D13 | Xác nhận báo cáo trên lượt có định danh | app_api | `identifier_stats.confirmed_report_count` tăng 1 (trigger), dù app_api không có quyền ghi bảng đó |
| D14 | Kháng nghị một quyết định không phải từ chối/ẩn | app_api | Lỗi `appeal.target_invalid` |
| D15 | Kháng nghị thứ hai cho cùng quyết định | app_api | Lỗi UNIQUE |
| D16 | Cán bộ đã ẩn tin tự xử lý kháng nghị | app_api | Lỗi `appeal.decider_conflict` |
| D17 | Chủ trọ (không phải sinh viên) viết đánh giá | app_api | Lỗi `review.not_student` |
| D18 | Xóa lượt kiểm tra hết hạn | app_maintenance | Ảnh, kết quả, định danh của lượt, báo cáo trên lượt bị xóa theo CASCADE; `identifier_stats` giữ nguyên |
| D19 | Job hết hạn tin | app_maintenance | Tin `verified` quá hạn thành `expired` kèm dòng chuyển trạng thái của system |
| D20 | Kích hoạt phiên bản bộ gộp thứ hai khi chưa tắt bản cũ | app_api | Lỗi UNIQUE `uq_model_versions_one_active` |
| D21 | Full-text không dấu: tìm "phong tro khep kin" | app_api | Khớp tin "Phòng trọ khép kín" |
| D22 | `REFRESH MATERIALIZED VIEW CONCURRENTLY` | app_maintenance | Thành công (cần quyền `MAINTAIN` + `TEMPORARY`) |
| D23 | Ghi chuyển trạng thái `expired → draft`; rồi `hidden → draft` | app_api | Cạnh đầu thành công (0003); cạnh sau lỗi CHECK `ck_listing_transitions_allowed` |
| D24 | `assessments.price_vnd` khác NULL khi `price_signal` NULL, hoặc ngoài 100.000–100.000.000 | app_worker | Lỗi CHECK `ck_assessments_price_vnd` |
| D25 | Đọc tiện nghi `private_bathroom` sau 0003 | app_api | `is_active = false` |
| D26 | Ẩn danh hóa chỉ NULL `email`, `phone_e164` mà giữ `email_verified_at` / `phone_verified_at` | app_api | Lỗi CHECK `ck_users_email_verified_has_email` / `ck_users_phone_verified_has_phone` (vì vậy US-106 NULL cả bốn cột) |

## Kiểm thử API

- Fixture tạo người dùng theo vai trò và trạng thái xác minh, trả access token; fixture tạo tin ở từng trạng thái.
- **Ma trận phân quyền**: một test tham số hóa duyệt mọi endpoint trong bảng 04-api.md × 6 kiểu người dùng
  (khách, student, owner, moderator, admin, student-chưa-xác-thực) và so mã trạng thái với kỳ vọng. Thêm
  endpoint mới mà không thêm dòng vào ma trận → test thất bại (quét `EndpointDataSource`).
- **Chống IDOR**: người dùng B sửa, gửi duyệt, xóa ảnh, xem lịch sử tin **chưa công khai** của người dùng A → `404`;
  B gọi thao tác chủ tin (gỡ, gia hạn) trên tin **đang công khai** của A → `403 listing.not_owner`.
- **Lượt sàng lọc tin không lộ** (BR-20): `GET /scans/{scanId}`, `Watch`, báo cáo với `scanId` của lượt `listing` → `404`;
  `/me/scans` của người đăng không có lượt đó.
- **Tách nhiệm vụ** (BR-19): cán bộ duyệt / ẩn tin của chính mình, xác minh hồ sơ của chính mình, xử lý kháng nghị về
  tin của mình → `403 moderation.conflict_of_interest`.
- **Kháng nghị đang mở**: khôi phục, gỡ, đưa về nháp tin có kháng nghị mở → `409 appeal.pending`; chấp nhận kháng nghị
  cho tin `rejected` → `verified` có `expires_at` và có outbox embedding.
- **Xóa tài khoản** (US-106): mọi tin chưa gỡ thành `removed`; còn tin `screening` → `409 me.listing_in_screening`;
  access token cũ bị từ chối ngay.
- **Đồng thời**: hai request duyệt cùng `version` chạy song song → một `200`, một `409 listing.version_conflict`.
- **Tệp tải lên**: PNG đổi đuôi `.jpg` (được, vì xét magic bytes), PDF đổi đuôi `.jpg` (`415`), ảnh 6 MB (`413`),
  ảnh 12.000 × 12.000 px (`415`), ảnh có EXIF GPS → ảnh lưu ra không còn EXIF.
- **Phiên**: refresh token xoay vòng; dùng lại token cũ → cả họ bị thu hồi; thiếu `X-Requested-With` → `403`;
  khóa tài khoản hoặc đặt lại mật khẩu → access token cũ bị từ chối trong ≤ 30 s; đổi vai trò → refresh ra token có quyền mới.
- **Số điện thoại**: gửi OTP cho số của người khác (chưa xác thực xong) không chặn được chủ số thật đăng ký (BR-12).
- **Hạn mức**: lượt kiểm tra thứ 6 của khách trong ngày → `429` có `Retry-After`; request bị `422`/`415` không trừ lượt;
  `GET /scans/quota` khớp số lượt đã dùng.
- **Outbox**: tạo lượt kiểm tra khi Redis tắt → API vẫn trả `202`; bật lại Redis → việc được đẩy đi; lượt `queued`
  quá 5 phút có outbox đã đẩy → được đẩy lại.
- **Consumer**: sự kiện `scan.completed` của lượt cũ (giao lại) khi tin đang sàng lọc lượt mới → tin vẫn ở `screening`.
- **Link giấy tờ**: hết hạn 5 phút kiểm bằng `TimeProvider` giả ở test tích hợp (không chờ thật).

## Kiểm thử worker

| Module | Ca bắt buộc |
|---|---|
| `identifiers` | `0912 345 678`, `0912.345.678`, `+84 912345678`, `84912345678` → cùng `+84912345678`; số bàn, số tiền 10 chữ số không bị nhận nhầm là số điện thoại; số tài khoản chỉ nhận khi gần từ khóa; **test vector HMAC dùng chung với test C#** (khóa base64 đã giải mã, đầu vào `phone:+84912345678`, băm hex cố định) |
| `pii` | ≥ 30 câu thật đã ẩn danh: số điện thoại, số tài khoản, email, link Facebook, "anh Tuấn", "chị Nguyễn Thị Lan", CCCD; không che nhầm tên đường, tên trường |
| `injection` | Câu chèn tiếng Việt có dấu / không dấu / tiếng Anh; câu bình thường chứa "hệ thống điều hòa" không bị bắt nhầm; câu trích là câu còn dấu của văn bản đã che; chuỗi `</TIN>` trong tin bị vô hiệu trước khi vào prompt |
| `price` | "1tr8" → 1.800.000; "1,8 triệu"; "1.800.000đ"; "1800k"; "2tr5/tháng"; bỏ số ngoài 300k–20tr; tin việc "lương 5 triệu/tháng" không có từ khóa thuê phòng → không trích giá; có tín hiệu thì có `price_vnd` |
| `text_signal` | Câu trích không khớp bị loại; câu trích < 6 ký tự bị loại; mã tiêu chí lạ bị loại; mọi dấu hiệu bị loại → `text_signal ≤ 0,3`; LLM timeout / `429` → luật dự phòng, `llm_model_id = None`; `LLM_MODEL` lệch `model_versions.name` → chỉ dùng luật |
| `image_signal` | Ảnh cắt 20% vẫn khớp; chỉ khớp tin khác của chính người đăng → 0; không so CLIP khác `clip_model_id` |
| `aggregate` | Các ví dụ số ở 05-ai-worker.md (gồm 98 của ví dụ 04-api.md); ca làm tròn `risk = 0,765` → 77 (không phải 76); `injectionFloor`; tín hiệu NULL bị bỏ qua |
| `scan_job` (tích hợp) | Một lượt đầy đủ với LLM giả → đúng dòng trong `assessments`, `flags`, `scan_identifiers`; giao lại lần 2 → không thêm dòng **và vẫn phát lại `scan.completed`**; trúng cache → có `scan_identifiers`; lượt `listing` không dùng cache; lỗi 3 lần → `failed` + `completed_at` + dead letter |

## Đầu cuối (Playwright)

Chạy trên `docker compose` đầy đủ (workflow `nightly.yml`) với LLM giả — `LLM_FAKE=1` và dòng `llm`
`fake/deterministic` do script E2E đăng ký (05-ai-worker.md, mục Sổ đăng ký) — dữ liệu từ `db/seed/demo/`
(tài khoản đã xác thực sẵn). Chỉ dùng luật thì một tiêu chí khớp chỉ ra mức Thấp, nên kịch bản 1 cần LLM giả.

1. **Khách Kiểm tra tin**: dán tin đòi cọc → thấy mức "Cao", dấu hiệu "Đòi cọc trước khi cho xem phòng" và câu trích.
2. **Đăng và duyệt tin**: chủ trọ tạo tin phòng, tải ảnh, gửi duyệt → cán bộ thấy trong hàng đợi, duyệt → tin
   xuất hiện khi khách tìm "phòng khép kín".
3. **Báo cáo → ẩn → kháng nghị**: sinh viên báo cáo tin → cán bộ A xác nhận và ẩn → chủ trọ kháng nghị → cán bộ A
   không xử lý được → admin chấp nhận → tin công khai lại.
4. **Giấy tờ**: chủ trọ tải giấy tờ → cán bộ nhập mục đích, mở được link; nhật ký có dòng truy cập. (Hết hạn sau
   5 phút kiểm ở test tích hợp API với `TimeProvider` giả, không chờ 5 phút trong E2E.)

## Kiểm thử tải (k6, thư mục `tests/load/`)

| Kịch bản | Tải | Ngưỡng đạt |
|---|---|---|
| `browse.js` — trang chủ, tìm tin có lọc, xem chi tiết | 50 người dùng ảo, 5 phút | `http_req_duration{api}` p95 < 500 ms, lỗi < 1% |
| `search.js` — tìm câu tự nhiên | 20 người dùng ảo, 3 phút | p95 < 1.000 ms |
| `quick-check.js` — gửi lượt kiểm tra (chữ + 2 ảnh chụp màn hình), poll kết quả mỗi 0,5 s | `constant-arrival-rate` 6 lượt/phút trong 5 phút (≈ 10 lần tải dự kiến khi 30 sinh viên thử nghiệm), 1–2 tiến trình worker, cấu hình LLM như production | p95 của `completedAt − createdAt` (lấy từ `GET /scans/{id}`, đo phía máy chủ) < 10 s; lỗi < 1% |

Không dùng kịch bản "N người dùng ảo vòng kín" cho `quick-check.js`: mỗi worker xử lý tuần tự một lượt (~5 s), nên
10 người dùng ảo gửi liên tục làm hàng đợi dồn ~50 s và p95 vỡ dù hệ thống đủ cho tải thật. Muốn đo sức chịu tối đa
thì tăng dần tốc độ đến cho tới khi p95 vượt 10 s và ghi lại ngưỡng đó.

Chạy trên production trước khi mở thử nghiệm (ngoài giờ cao điểm), tạm nâng hạn mức `POST /scans` cho IP của máy
chạy k6, và ghi kết quả vào báo cáo (thí nghiệm E6).

## Kiểm thử bảo mật

- OWASP ZAP baseline (`zaproxy/action-baseline`) hằng đêm trên stack Docker đầy đủ trong workflow `nightly.yml`
  (dự án không có staging); cảnh báo mức High chặn release.
- Ca thủ công trước buổi bảo vệ: thử truy cập `/files/documents/{token}` với token hết hạn, sửa một byte chữ ký;
  thử XSS trong tiêu đề/mô tả tin và câu trích; thử SQL injection ở tham số `sort`, `q`.
- Thí nghiệm E4 (câu chèn) là kiểm thử bảo mật của pipeline AI.

## Nghiệm thu

PR template có mục "User story / AC được phủ" và "Cách kiểm thử"; người review (hoặc chính bạn khi tự review)
đối chiếu từng AC. Trước khi chốt tính năng (cuối tuần 11): mọi story mức **M** có test hoặc kịch bản tay đã chạy.
