# Trọ & Việc Sạch — bộ tài liệu để vibe code

Bộ tài liệu kỹ thuật cho đồ án *"Xây dựng nền tảng phòng trọ và việc làm thêm đã xác minh cho sinh viên,
ứng dụng AI phát hiện tin đăng lừa đảo"*. Mục đích: đặt vào gốc repo để AI coding agent (Claude Code, Cursor,
Copilot…) xây toàn bộ hệ thống theo từng task, đúng kiến trúc và đúng đề cương.

## Có gì trong bộ này

| Đường dẫn | Nội dung |
|---|---|
| `AGENTS.md` | Luật làm việc cho mọi AI agent: stack, lệnh, quy tắc bắt buộc, định nghĩa "xong" |
| `CLAUDE.md` | Nạp `AGENTS.md` + ghi chú riêng cho Claude Code |
| `docs/00`–`09` | Tổng quan, yêu cầu, kiến trúc, CSDL, API, AI worker, giao diện, bảo mật, triển khai, kiểm thử |
| `docs/10-implementation-plan.md` | 38 task theo 6 giai đoạn, mỗi task có prompt dán thẳng cho agent và tiêu chí xong |
| `db/init/00_init.sh` | Tạo extension, 5 role đăng nhập, schema (superuser, chạy một lần) |
| `db/migrations/0001_init.sql` | Lược đồ đầy đủ 32 bảng, ràng buộc, trigger, materialized view, phân quyền |
| `db/migrations/0002_seed_reference.sql` | Vai trò, quyền, tiện nghi, bộ tiêu chí, phiên bản mô hình ban đầu |
| `db/seed/administrative_units/README.md` | Cách nạp 34 tỉnh, 3.321 xã |
| `.env.example` | Toàn bộ biến môi trường |

## Cách dùng

1. Tạo repo mới, chép toàn bộ thư mục này vào gốc repo, commit lần đầu.
2. Đọc `docs/00-overview.md` và `docs/01-requirements.md` (khoảng 30 phút) để nắm hệ thống — bạn là người
   duyệt kế hoạch và đọc diff của agent, nên cần hiểu sản phẩm trước.
3. Chốt các việc ở mục "Việc bạn cần quyết định" bên dưới.
4. Mở agent ở gốc repo, làm lần lượt từ **T0.1** trong `docs/10-implementation-plan.md`: mỗi task một nhánh,
   một phiên làm việc, một PR. Dán prompt của task, duyệt kế hoạch agent đưa ra, rồi cho code.
5. Khi quyết định thay đổi (đổi thư viện, thêm bảng, đổi endpoint), sửa tài liệu trong cùng PR để tài liệu luôn là
   nguồn sự thật cho các task sau.

## Thay đổi so với đề cương

| Đề cương | Bộ tài liệu này | Lý do |
|---|---|---|
| ASP.NET Core 8 | **.NET 10 (LTS)** | .NET 8 hết hỗ trợ ngày 10/11/2026; .NET 10 được hỗ trợ đến 11/2028 |
| MinIO | **Garage** (S3 API) | MinIO Community ngừng phát hành bản build từ 10/2025, repo bị lưu trữ năm 2026; code dùng S3 API nên đổi nhà cung cấp chỉ là đổi cấu hình |
| Giấy tờ mã hóa phía máy chủ MinIO + presigned URL | Mã hóa AES-256-GCM ở tầng ứng dụng, xem qua API bằng link 5 phút | Không phụ thuộc tính năng của storage |
| 31 bảng | **32 bảng**: thêm `scan_images` và các cột cần cho cài đặt (`scans.status`, `scans.public_id`, `listings.description`, tín hiệu trong `assessments`…) | Worker cần chỗ lưu ảnh người dùng gửi khi Kiểm tra tin |
| Bảng phân quyền | Sinh viên được kháng nghị tin của mình; thêm quyền lưu tin, đánh giá | Bạn đã cho sinh viên đăng tin; lưu tin và đánh giá có trong phần yêu cầu |
| Quản trị viên chỉnh ngưỡng điểm | Ngưỡng thuộc phiên bản bộ gộp; quản trị viên đổi ngưỡng bằng cách kích hoạt phiên bản khác | Ngưỡng phải được đánh giá trên tập validation trước khi dùng thật |

Những thay đổi này chưa được cập nhật vào đề cương; nên sửa đề cương cho khớp trước khi nộp.

## Đã kiểm chứng

- `0001_init.sql` và `0002_seed_reference.sql` đã chạy thành công trên PostgreSQL 16 + PostGIS + pgvector 0.6 cùng
  22 ca kiểm thử ràng buộc, trigger và phân quyền theo role (danh sách ở `docs/09-testing.md`).
- Hai câu lệnh cần bản mới hơn chưa chạy được ở môi trường kiểm thử: `GRANT MAINTAIN` (PostgreSQL 17+) và
  `SET hnsw.iterative_scan` (pgvector 0.8+). Task T0.3 chạy migration trên đúng PostgreSQL 18 sẽ xác nhận.
- Phiên bản thư viện ở `docs/02-architecture.md` ghi theo major; agent ghim bản cụ thể vào lockfile khi tạo project.

## Việc bạn cần quyết định

| Việc | Ảnh hưởng | Gợi ý |
|---|---|---|
| Tên miền email sinh viên của trường | `Auth__StudentEmailDomains` (BR-13) | Hỏi phòng đào tạo / trung tâm thông tin |
| Nhà cung cấp LLM và mô hình | Chất lượng tín hiệu chữ, chi phí, chuyển dữ liệu ra nước ngoài | Chọn mô hình nhanh có structured output; đọc điều khoản lưu dữ liệu; đăng ký bằng migration `0003` |
| Nhà cung cấp SMS OTP | Chi phí; tin nhắn thương hiệu cần đăng ký | Dev dùng `console`; thử nghiệm thật có thể dùng tài khoản đã xác thực sẵn do cán bộ cấp |
| VPS và tên miền | Bắt buộc từ tuần 4 | 4 vCPU / 8 GB RAM trở lên |
| Đơn vị thử nghiệm | Có cán bộ xác minh thật hay không | Xác nhận trước hết tuần 4 (rủi ro trong đề cương) |

## Nguồn đã dùng

- .NET 8 và 9 hết hỗ trợ 10/11/2026: [.NET Blog](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/)
- Tình trạng MinIO Community và các phương án thay thế: [It's FOSS](https://itsfoss.com/news/minio-moves-away-from-open-source/), [InfoQ](https://www.infoq.com/news/2025/12/minio-s3-api-alternatives)
- Image PostGIS cho PostgreSQL 18: [postgis/docker-postgis](https://github.com/postgis/docker-postgis)
- Next.js 16: [nextjs.org/blog](https://nextjs.org/blog)
- Danh mục đơn vị hành chính 34 tỉnh, 3.321 xã: [Quyết định 19/2025/QĐ-TTg (Báo Lào Cai)](https://baolaocai.vn/bang-danh-muc-va-ma-so-cua-34-tinh-thanh-moi-3321-don-vi-hanh-chinh-cap-xa-moi-post648007.html), [vietnamese-provinces-database](https://github.com/ThangLeQuoc/vietnamese-provinces-database/releases)
