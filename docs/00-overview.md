# 00 — Tổng quan sản phẩm

> Bộ tài liệu này là **nguồn sự thật** cho việc xây dựng hệ thống. Khi code và tài liệu
> mâu thuẫn, sửa một trong hai cho khớp trong cùng commit. Đề cương đồ án là bản gốc về
> mục tiêu; tài liệu này là bản kỹ thuật chi tiết của đề cương đó.

## Sản phẩm trong một câu

**Trọ & Việc Sạch** (tên tạm, code name `troviec`) là nền tảng web (PWA) giúp sinh viên tìm
phòng trọ và việc làm thêm **đã được xác minh**, và cho phép bất kỳ ai **kiểm tra một tin đăng
bên ngoài** (dán chữ hoặc ảnh chụp màn hình) để nhận điểm rủi ro lừa đảo 0–100 kèm bằng chứng.

## Bài toán

- Lừa đảo phòng trọ (đòi cọc trước khi xem phòng, ảnh lấy lại, giá rẻ bất thường) và việc làm
  thêm (thu phí hồ sơ, việc nhẹ lương cao, dẫn đến khoản vay) lặp lại liên tục trong các group
  sinh viên.
- Công cụ hiện có chủ yếu tra cứu danh tính đã bị báo cáo, nên chỉ cảnh báo được sau khi đã có
  người bị lừa; kẻ gian đổi tài khoản ngay khi bị lộ.
- Hệ thống lấp khoảng trống bằng hai việc: **phân tích chính nội dung và hình ảnh tin** trước khi
  sinh viên liên hệ, và **kênh tin đã được nhà trường xác minh**.

## Người dùng

| Tác nhân | Mô tả | Vai trò trong hệ thống |
|---|---|---|
| Khách | Chưa đăng nhập | Không có vai trò; Kiểm tra tin 5 lượt/ngày, xem và tìm tin đã xác minh |
| Sinh viên | Đăng ký bằng email trường | `student` |
| Chủ trọ / nhà tuyển dụng | Xác thực số điện thoại, nộp giấy tờ | `owner` |
| Cán bộ xác minh | Đoàn Thanh niên / đơn vị công tác sinh viên | `moderator` |
| Quản trị viên | Vận hành hệ thống | `admin` |

Một tài khoản có thể có nhiều vai trò. Quyền chi tiết: [01-requirements.md](01-requirements.md#ma-trận-phân-quyền).

## Phạm vi MVP

| Hạng mục | Trong phạm vi (MVP) | Ngoài phạm vi |
|---|---|---|
| Kiểm tra tin | Dán văn bản hoặc tải ảnh chụp màn hình; điểm rủi ro kèm bằng chứng | Dán link Facebook để hệ thống tự đọc (cần đăng nhập, rủi ro SSRF) |
| Tin đăng | Phòng trọ và việc làm thêm của bên đã xác minh, có địa điểm thật | Nhà nguyên căn, đồ cũ, việc online không có địa điểm |
| Địa bàn | Quanh các cơ sở đào tạo của trường | Toàn Hà Nội, nhiều trường |
| Xác minh | OTP số điện thoại; cán bộ duyệt giấy tờ do bên đăng tải lên | eKYC tự động bằng CCCD gắn chip hoặc VNeID |
| AI | Chấm rủi ro văn bản, ảnh dùng lại, giá bất thường, tìm kiếm ngữ nghĩa | Đọc hợp đồng thuê; kiểm tra ghi âm giọng thật / giọng AI |
| Tiền | Không nhận, không giữ tiền của bất kỳ ai | Giữ cọc hộ (thuộc diện trung gian thanh toán) |
| Nền tảng | PWA chạy trên trình duyệt điện thoại và máy tính | App native iOS/Android |
| Nguồn dữ liệu | Chỉ nội dung người dùng chủ động gửi | Tự động thu thập bài từ Facebook (vi phạm điều khoản) |

## Nguyên tắc sản phẩm (không được vi phạm khi code)

1. **AI không kết luận.** Hệ thống chỉ nói "có dấu hiệu rủi ro" ở ba mức *Thấp / Cần thận trọng /
   Cao*. Không bao giờ hiển thị chữ "lừa đảo" gắn với một người hay một tin cụ thể.
2. **Con người quyết định.** Mọi việc công khai, ẩn, gỡ tin do cán bộ xác minh thực hiện; AI chỉ
   sắp hàng đợi và đưa bằng chứng.
3. **Có bằng chứng mới có dấu hiệu.** Mỗi dấu hiệu phải trích nguyên văn câu trong tin; câu trích
   không khớp đầu vào thì dấu hiệu bị loại.
4. **Riêng tư mặc định.** Che số điện thoại, số tài khoản, họ tên trước khi gửi ra LLM bên ngoài;
   định danh của bên thứ ba chỉ lưu dạng HMAC; lượt kiểm tra của khách tự xóa sau 30 ngày.
5. **Bên bị ảnh hưởng luôn được kháng nghị** trong 7 ngày, và người xử lý phải khác người đã ra
   quyết định.
6. **Không cầm tiền.** Không có chức năng thanh toán, đặt cọc hay giữ tiền.

## Chỉ tiêu (đặt trước, kết quả thật báo cáo ở chương thực nghiệm)

| Mục tiêu | Chỉ số | Chỉ tiêu |
|---|---|---|
| Chấm rủi ro tin | Recall / Precision lớp "rủi ro cao", tập test ≥ 300 tin thật | Recall ≥ 0,85; Precision ≥ 0,80 |
| Phát hiện ảnh dùng lại | Precision@1 trên ảnh bị cắt, chèn chữ, nén lại | ≥ 0,90 |
| Cảnh báo có bằng chứng | Tỉ lệ trích đúng câu làm bằng chứng (100 mẫu kiểm tay) | ≥ 90% |
| Tốc độ kiểm tra tin | p95 từ lúc tải ảnh đến lúc có kết quả | ≤ 10 giây |
| Tìm kiếm câu tự nhiên | nDCG@10 trên 30 truy vấn mẫu so với tìm theo từ khóa | Cao hơn ≥ 10% |
| Vận hành thật | Uptime trong thời gian thử nghiệm | ≥ 99% |
| Thử nghiệm người dùng | Số sinh viên dùng thử; điểm SUS | ≥ 30 người; SUS ≥ 70 |

## Thuật ngữ (tiếng Việt ↔ tên trong code)

Dùng đúng tên trong cột "Code" cho bảng, cột, class, endpoint. Giao diện dùng cột "Tiếng Việt".

| Tiếng Việt | Code | Ghi chú |
|---|---|---|
| Tin đăng | `listing` | Phần chung của tin phòng và tin việc |
| Tin phòng / tin việc | `room_listing` / `job_listing` | Bảng con 1–1 của `listings` |
| Kiểm tra tin | `quick_check` | Giá trị `scans.source` |
| Lượt sàng lọc | `scan` | Một lần chạy pipeline AI (kiểm tra nhanh hoặc sàng lọc tin trước khi duyệt) |
| Kết quả chấm | `assessment` | Điểm 0–100 + mức rủi ro + 4 tín hiệu |
| Dấu hiệu, tiêu chí | `flag`, `criterion` | Dấu hiệu = một tiêu chí khớp, kèm câu trích |
| Mức rủi ro Thấp / Cần thận trọng / Cao | `low` / `caution` / `high` | Enum `risk_level` |
| Định danh | `identifier` | Số điện thoại, số tài khoản, đường link (dạng HMAC) |
| Báo cáo | `report` | Báo cáo một tin hoặc một lượt kiểm tra |
| Kháng nghị | `appeal` | Phản đối quyết định từ chối / ẩn tin |
| Hồ sơ chủ trọ / nhà tuyển dụng | `owner_profile` | Bảng con 1–1 tùy chọn của `users` |
| Giấy tờ xác minh | `owner_document` | Mã hóa AES-256-GCM ở tầng ứng dụng |
| Cán bộ xác minh | `moderator` | |
| Hàng đợi duyệt | moderation queue | Tin `pending_review` sắp theo điểm AI |
| Vòng đời tin | listing transitions | Bảng `listing_transitions`, chỉ thêm |
| Đơn vị hành chính (tỉnh, xã) | `administrative_unit` (`province`, `commune`) | Hai cấp sau 01/7/2025 |

## Bản đồ tài liệu

| File | Nội dung |
|---|---|
| [01-requirements.md](01-requirements.md) | User story, tiêu chí nghiệm thu, quy tắc nghiệp vụ, vòng đời tin, ma trận quyền |
| [02-architecture.md](02-architecture.md) | Thành phần, luồng xử lý, cấu trúc repo, quyết định kiến trúc, phiên bản |
| [03-database.md](03-database.md) | Quy ước CSDL, ý nghĩa NULL, truy vấn mẫu cho Dapper |
| [04-api.md](04-api.md) | REST API, SignalR, mã lỗi, hạn mức |
| [05-ai-worker.md](05-ai-worker.md) | Pipeline AI, hợp đồng hàng đợi, prompt LLM, bộ gộp điểm, đánh giá |
| [06-frontend.md](06-frontend.md) | Màn hình, PWA, quy ước giao diện và câu chữ |
| [07-security.md](07-security.md) | Mô hình đe dọa và các kiểm soát bắt buộc |
| [08-deployment.md](08-deployment.md) | Docker Compose, Caddy, CI/CD, sao lưu, giám sát, biến môi trường |
| [09-testing.md](09-testing.md) | Chiến lược kiểm thử, kiểm thử AI, kiểm thử tải |
| [10-implementation-plan.md](10-implementation-plan.md) | Các task theo thứ tự, kèm prompt cho AI agent và tiêu chí xong |
| [../db/migrations/](../db/migrations/) | Lược đồ CSDL thật (32 bảng), đã chạy thử |
