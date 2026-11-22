# AGENTS.md — hướng dẫn cho AI coding agent

Đọc file này trước mọi việc. Nó áp dụng cho mọi agent (Claude Code, Cursor, Copilot, Codex…).

## Dự án

**Trọ & Việc Sạch** (code name `troviec`): PWA giúp sinh viên tìm phòng trọ và việc làm thêm đã được xác minh,
kèm tính năng **Kiểm tra tin** — AI chấm điểm rủi ro lừa đảo 0–100 cho một tin bên ngoài, có bằng chứng.
Đây là đồ án tốt nghiệp: mọi đoạn code phải giải thích được trước hội đồng, nên ưu tiên rõ ràng hơn khéo léo.

## Tài liệu (nguồn sự thật)

| Cần biết | Đọc |
|---|---|
| Sản phẩm, thuật ngữ, nguyên tắc | `docs/00-overview.md` |
| User story, quy tắc nghiệp vụ, vòng đời tin, ma trận quyền | `docs/01-requirements.md` |
| Thành phần, luồng, cấu trúc repo, phiên bản | `docs/02-architecture.md` |
| CSDL: quy ước, NULL, truy vấn mẫu, ánh xạ lỗi | `docs/03-database.md` + `db/migrations/*.sql` |
| Endpoint, mã lỗi, SignalR, hạn mức | `docs/04-api.md` |
| Pipeline AI, hàng đợi, prompt LLM, bộ gộp điểm | `docs/05-ai-worker.md` |
| Trang, PWA, câu chữ giao diện | `docs/06-frontend.md` |
| Kiểm soát bảo mật bắt buộc | `docs/07-security.md` |
| Docker, Caddy, CI/CD, biến môi trường | `docs/08-deployment.md` |
| Test cần viết | `docs/09-testing.md` |
| Task hiện tại và mục cần đọc | `docs/10-implementation-plan.md` |
| Tầm nhìn sau đồ án; quản lý trọ (Giai đoạn 1, task T6.x) | `docs/11-product-vision.md`; `docs/12-rental.md` |

Chỉ đọc các mục task yêu cầu; đừng nạp cả bộ tài liệu vào ngữ cảnh nếu không cần.

## Stack (không tự đổi)

- **api**: .NET 10, ASP.NET Core Minimal API, vertical slice (`Features/*`), Dapper + Npgsql, FluentValidation, xUnit v3 + Shouldly + Testcontainers.
- **worker**: Python 3.12, uv, psycopg 3, redis-py, pydantic 2, FastAPI (chỉ `/internal/embed`), pytest, ruff.
- **web**: Next.js 16 App Router, React 19, TypeScript strict, Tailwind 4, shadcn/ui, TanStack Query, openapi-fetch, Vitest, Playwright, pnpm.
- **hạ tầng**: PostgreSQL 18 + PostGIS + pgvector, Redis 8 (Streams), Garage (S3), Caddy, Docker Compose.

Thêm thư viện ngoài danh sách ở `docs/02-architecture.md` (mục Phiên bản công nghệ) → **hỏi trước**.

## Lệnh

```bash
make dev-up      # postgres, redis, garage, mailpit
make migrate     # chạy db/migrations bằng TroViec.Migrator
make api         # dotnet watch
make worker      # uv run python -m troviec_worker consume
make worker-embed # uv run python -m troviec_worker serve-embed (cần cho tìm kiếm câu tự nhiên)
make web         # pnpm dev
make gen-api     # sinh apps/web/lib/api/schema.d.ts từ /openapi/v1.json
make test        # dotnet test + pytest + vitest
```

## Quy tắc bắt buộc

**Cách làm việc**
1. Trước khi code: trình bày kế hoạch ngắn (file tạo/sửa, test sẽ viết, câu hỏi còn mở) và chờ duyệt.
2. Làm đúng phạm vi task; không refactor, đổi tên, "dọn dẹp" ngoài phạm vi.
3. Tài liệu mơ hồ hoặc mâu thuẫn → dừng lại hỏi, nêu các phương án. Không tự bịa yêu cầu.
4. Code khác tài liệu → sửa code cho khớp, hoặc đề xuất sửa tài liệu trong cùng PR (nói rõ vì sao).
5. Mọi thay đổi hành vi có test. Không xóa, `skip` hay nới test để CI xanh.

**CSDL**
6. Lược đồ chỉ đổi bằng **migration mới** `db/migrations/NNNN_mo_ta.sql`. Không sửa migration đã có, nhất là `0001`, `0002`.
7. Bảng/cột mới phải `GRANT` tường minh cho đúng role ngay trong migration đó.
8. SQL luôn tham số hóa; ghi rõ schema `app.`; enum truyền chuỗi và cast `@Status::app.listing_status`; tên cột sắp xếp lấy từ whitelist.
9. Đổi `listings.status` **chỉ** qua `ListingTransitionService` (ghi `listing_transitions` cùng transaction, kiểm `version`).
   Ngoại lệ duy nhất: hai câu lệnh hàng loạt của job đêm ghi ở `docs/03-database.md` (mục Job đêm).
10. Không UPDATE/DELETE bảng chỉ-thêm: `listing_transitions`, `assessments`, `assessment_flags`, `assessment_image_matches`, `document_access_logs`
    (từ migration `0005`: thêm `payments`, xem `docs/12-rental.md`).

**Bảo mật và riêng tư**
11. Mọi endpoint khai báo `RequireAuthorization("<permission>")` hoặc `AllowAnonymous()` tường minh; riêng endpoint chỉ đụng
    dữ liệu của chính người gọi (`/me`, `/auth/phone/*`) dùng `RequireAuthorization()` không kèm quyền. Phân quyền theo
    **quyền**, không theo tên vai trò.
12. Kiểm tra quyền sở hữu tài nguyên; tài nguyên người khác không được xem → `404`. Cán bộ không xử lý việc liên quan tới
    chính mình (`moderation.conflict_of_interest`, BR-19).
13. Không log mật khẩu, token, cookie, số điện thoại, email, số tài khoản, nội dung tin, câu trả lời LLM — chỉ log id.
14. Văn bản chỉ gửi ra LLM sau khi qua `pipeline/pii.py`; không gửi ảnh ra LLM.
15. Không commit bí mật; mọi cấu hình đọc từ biến môi trường (`.env.example` là danh sách đầy đủ).
16. Không fetch URL do người dùng gửi (chống SSRF).

**Sản phẩm**
17. Chữ giao diện tiếng Việt. Không bao giờ hiển thị chữ "lừa đảo" gắn với một tin hay một người; mức rủi ro là *Thấp / Cần thận trọng / Cao*, luôn có chữ + biểu tượng, không chỉ màu.
18. Tiền là số nguyên VND; thời gian lưu UTC, hiển thị theo `Asia/Ho_Chi_Minh`.

## Quy ước code

- **Chung**: tên biến, hàm, comment, commit message bằng tiếng Anh; chữ giao diện và tài liệu bằng tiếng Việt.
  Commit theo Conventional Commits (`feat(scans): …`). Hàm ngắn, tên nói rõ ý.
- **C#**: nullable bật, file-scoped namespace, `record` cho request/response, hậu tố `Async`, truyền `CancellationToken`,
  không state tĩnh có thể đổi; mỗi feature tự chứa endpoint + validator + SQL; lỗi nghiệp vụ ném `ProblemException("<mã lỗi>")`.
  Tên test `Method_Condition_Expected`.
- **Python**: type hint đầy đủ, `ruff format` + `ruff check`, pydantic cho dữ liệu vào/ra, `logging` có cấu trúc (không `print`),
  các bước pipeline là hàm thuần nhận và trả dữ liệu để dễ test.
- **TypeScript**: `strict`, không `any`; server component mặc định, `"use client"` khi thật cần; gọi API qua `lib/api/client`
  (không `fetch` thẳng); form dùng react-hook-form + zod; không sửa tay `lib/api/schema.d.ts`.

## Định nghĩa "xong"

- Mọi tiêu chí nghiệm thu của các story trong task có test tự động (hoặc kịch bản tay ghi trong PR).
- `make test` xanh, lint sạch, build không cảnh báo.
- Migration mới (nếu có) chạy được trên CSDL rỗng.
- Hợp đồng đổi (API, CSDL, hàng đợi) thì tài liệu đã cập nhật và client TypeScript đã sinh lại.
- Mô tả PR: làm gì, vì sao, cách kiểm thử, AC đã phủ, giả định đã đưa ra.

## Khi bế tắc

Không đoán. Sửa hai lần chưa được thì dừng, tóm tắt nguyên nhân nghi ngờ và đề xuất bước chẩn đoán tiếp theo.
