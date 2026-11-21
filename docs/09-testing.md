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
| Bảo mật | OWASP ZAP baseline, test ma trận phân quyền | Không có cảnh báo mức High |
| Tải | k6 | Ngưỡng ở mục Kiểm thử tải |
| AI | Script E1–E5 (05-ai-worker.md) | Chỉ tiêu ở 00-overview.md |

## Kiểm thử CSDL

Các ca dưới đây đã chạy thử khi viết lược đồ; chuyển chúng thành test tự động (`TroViec.Api.Tests/Database/`)
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

## Kiểm thử API

- Fixture tạo người dùng theo vai trò và trạng thái xác minh, trả access token; fixture tạo tin ở từng trạng thái.
- **Ma trận phân quyền**: một test tham số hóa duyệt mọi endpoint trong bảng 04-api.md × 6 kiểu người dùng
  (khách, student, owner, moderator, admin, student-chưa-xác-thực) và so mã trạng thái với kỳ vọng. Thêm
  endpoint mới mà không thêm dòng vào ma trận → test thất bại (quét `EndpointDataSource`).
- **Chống IDOR**: người dùng B sửa, gửi duyệt, xóa ảnh, xem lịch sử tin của người dùng A → `404`.
- **Đồng thời**: hai request duyệt cùng `version` chạy song song → một `200`, một `409 listing.version_conflict`.
- **Tệp tải lên**: PNG đổi đuôi `.jpg` (được, vì xét magic bytes), PDF đổi đuôi `.jpg` (`415`), ảnh 6 MB (`413`),
  ảnh 12.000 × 12.000 px (`415`), ảnh có EXIF GPS → ảnh lưu ra không còn EXIF.
- **Phiên**: refresh token xoay vòng; dùng lại token cũ → cả họ bị thu hồi; thiếu `X-Requested-With` → `403`;
  khóa tài khoản → access token cũ bị từ chối trong ≤ 30 s.
- **Hạn mức**: lượt kiểm tra thứ 6 của khách trong ngày → `429` có `Retry-After`.
- **Outbox**: tạo lượt kiểm tra khi Redis tắt → API vẫn trả `202`; bật lại Redis → việc được đẩy đi.

## Kiểm thử worker

| Module | Ca bắt buộc |
|---|---|
| `identifiers` | `0912 345 678`, `0912.345.678`, `+84 912345678`, `84912345678` → cùng `+84912345678`; số bàn, số tiền 10 chữ số không bị nhận nhầm là số điện thoại; số tài khoản chỉ nhận khi gần từ khóa |
| `pii` | ≥ 30 câu thật đã ẩn danh: số điện thoại, số tài khoản, email, link Facebook, "anh Tuấn", "chị Nguyễn Thị Lan", CCCD; không che nhầm tên đường, tên trường |
| `injection` | Câu chèn tiếng Việt có dấu / không dấu / tiếng Anh; câu bình thường chứa "hệ thống điều hòa" không bị bắt nhầm |
| `price` | "1tr8" → 1.800.000; "1,8 triệu"; "1.800.000đ"; "1800k"; "2tr5/tháng"; bỏ số ngoài 300k–20tr |
| `text_signal` | Câu trích không khớp bị loại; câu trích < 6 ký tự bị loại; mã tiêu chí lạ bị loại; LLM timeout → luật dự phòng, `llm_model_id = None` |
| `aggregate` | Các ví dụ số ở 05-ai-worker.md; `injectionFloor`; tín hiệu NULL bị bỏ qua |
| `scan_job` (tích hợp) | Một lượt đầy đủ với LLM giả → đúng dòng trong `assessments`, `flags`, `scan_identifiers`; giao lại lần 2 → không thêm dòng; lỗi 3 lần → `failed` + dead letter |

## Đầu cuối (Playwright)

Chạy trên `docker compose` đầy đủ với LLM giả, dữ liệu từ `db/seed/demo/` (tài khoản đã xác thực sẵn).

1. **Khách Kiểm tra tin**: dán tin đòi cọc → thấy mức "Cao", dấu hiệu "Đòi cọc trước khi cho xem phòng" và câu trích.
2. **Đăng và duyệt tin**: chủ trọ tạo tin phòng, tải ảnh, gửi duyệt → cán bộ thấy trong hàng đợi, duyệt → tin
   xuất hiện khi khách tìm "phòng khép kín".
3. **Báo cáo → ẩn → kháng nghị**: sinh viên báo cáo tin → cán bộ A xác nhận và ẩn → chủ trọ kháng nghị → cán bộ A
   không xử lý được → admin chấp nhận → tin công khai lại.
4. **Giấy tờ**: chủ trọ tải giấy tờ → cán bộ nhập mục đích, mở được trong 5 phút; sau 5 phút link báo hết hạn;
   nhật ký có dòng truy cập.

## Kiểm thử tải (k6, thư mục `tests/load/`)

| Kịch bản | Tải | Ngưỡng đạt |
|---|---|---|
| `browse.js` — trang chủ, tìm tin có lọc, xem chi tiết | 50 người dùng ảo, 5 phút | `http_req_duration{api}` p95 < 500 ms, lỗi < 1% |
| `search.js` — tìm câu tự nhiên | 20 người dùng ảo, 3 phút | p95 < 1.000 ms |
| `quick-check.js` — gửi lượt kiểm tra, chờ kết quả bằng poll | 10 người dùng ảo, 3 phút, LLM thật | thời gian gửi → `completed` p95 < 10 s |

Chạy trên production trước khi mở thử nghiệm (ngoài giờ cao điểm) và ghi kết quả vào báo cáo (thí nghiệm E6).

## Kiểm thử bảo mật

- OWASP ZAP baseline (`zaproxy/action-baseline`) hằng đêm trên staging; cảnh báo mức High chặn release.
- Ca thủ công trước buổi bảo vệ: thử truy cập `/files/documents/{token}` với token hết hạn, sửa một byte chữ ký;
  thử XSS trong tiêu đề/mô tả tin và câu trích; thử SQL injection ở tham số `sort`, `q`.
- Thí nghiệm E4 (câu chèn) là kiểm thử bảo mật của pipeline AI.

## Nghiệm thu

PR template có mục "User story / AC được phủ" và "Cách kiểm thử"; người review (hoặc chính bạn khi tự review)
đối chiếu từng AC. Trước khi chốt tính năng (cuối tuần 11): mọi story mức **M** có test hoặc kịch bản tay đã chạy.
