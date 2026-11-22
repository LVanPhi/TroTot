# 10 — Kế hoạch vibe code

Hệ thống được dựng qua **38 task** theo 6 giai đoạn, bám khung 15 tuần trong đề cương: lên domain
HTTPS cuối tuần 4, Kiểm tra tin chạy được cuối tuần 8, chốt tính năng cuối tuần 11. Mỗi task có prompt
dán thẳng cho AI agent (Claude Code, Cursor, Copilot…) và danh sách "Xong khi".

## Cách làm một task

1. Tạo nhánh `feat/<mã-task>-<mô-tả>` từ `main`.
2. Mở phiên mới cho agent (Claude Code: `/clear`). Một task một phiên giúp ngữ cảnh gọn và ít sai.
3. Dán prompt của task. Prompt nào cũng bắt agent **lập kế hoạch trước**; đọc kế hoạch, sửa nếu lệch tài liệu,
   rồi mới cho code.
4. Agent tự chạy test. Bạn đọc diff, kỹ nhất ở SQL, xác thực, phân quyền và xử lý tệp — hội đồng sẽ hỏi
   về chính những chỗ này, nên không merge đoạn nào mình chưa hiểu.
5. Commit theo Conventional Commits (`feat(scans): …`, `fix(auth): …`), mở PR, đánh dấu AC đã phủ.
6. Agent làm khác tài liệu → chỉ ra đúng file và mục. Tài liệu sai → sửa tài liệu **trong cùng PR**.

Mẫu prompt chung (các prompt bên dưới đã theo mẫu này):

```text
Đọc AGENTS.md, rồi đọc: <các mục tài liệu>.
Nhiệm vụ: <mô tả>.
Phạm vi: <thư mục/file>. Không sửa ngoài phạm vi; nếu bắt buộc phải sửa thì nói rõ lý do.
Yêu cầu: <gạch đầu dòng>.
Xong khi: <tiêu chí>.
Trước khi viết code, trình bày kế hoạch ngắn (file tạo/sửa, test sẽ viết, câu hỏi còn mở) và chờ tôi duyệt.
```

## Tổng quan

| Giai đoạn | Tuần | Task | Mốc |
|---|---|---|---|
| 0. Khung và hạ tầng | 3–4 | T0.1–T0.8 | Hệ thống "hello world" chạy trên domain HTTPS |
| 1. Tài khoản, phân quyền | 5 | T1.1–T1.4 | Đăng ký, đăng nhập, phân quyền chạy đầu cuối |
| 2. Kiểm tra tin | 6–8 | T2.1–T2.7 | **Kiểm tra tin chạy được** (gặp GVHD cuối tuần 8) |
| 3. Tin đăng, kiểm duyệt, tìm kiếm | 7–10 | T3.1–T3.11 | Đủ tính năng MVP |
| 4. Hoàn thiện | 10–11 | T4.1–T4.5 | **Chốt tính năng** cuối tuần 11 |
| 5. Thử nghiệm, báo cáo | 12–15 | T5.1–T5.3 | Số liệu thật cho chương 5 |
| 6. Quản lý trọ (sau bảo vệ) | — | T6.1–T6.6 | Giai đoạn 1 của nền tảng; task và tiêu chí ở [12-rental.md](12-rental.md#task) |

Thu thập và gán nhãn dữ liệu chạy song song từ tuần 2 đến tuần 8 (không cần code, xem T4.5).

---

## Giai đoạn 0 — Khung và hạ tầng

### T0.1 Khởi tạo monorepo
- **Phụ thuộc:** không.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/02-architecture.md (mục Cấu trúc repo, Phiên bản công nghệ).
  Nhiệm vụ: khởi tạo khung monorepo đúng cây thư mục trong tài liệu (chưa có code nghiệp vụ).
  Phạm vi: thư mục gốc, apps/*, infra/, research/, .github/.
  Yêu cầu: .editorconfig, .gitignore (dotnet, python, node, .env), .gitattributes;
  Makefile với target: dev-up, dev-down, migrate, api, worker, worker-embed, web, test, gen-api (target chưa có thì in thông báo);
  .github/pull_request_template.md có mục "User story / AC được phủ" và "Cách kiểm thử";
  dependabot.yml cho nuget, pip, npm, github-actions, docker.
  Giữ nguyên AGENTS.md, CLAUDE.md, docs/, db/, .env.example đã có.
  Xong khi: `make help` liệt kê target; git status sạch sau commit.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```
- **Xong khi:** cây thư mục khớp tài liệu; không có file thừa.

### T0.2 Hạ tầng dev bằng Docker Compose
- **Phụ thuộc:** T0.1.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/08-deployment.md (mục Docker Compose dev, Garage, Biến môi trường), db/init/00_init.sh.
  Nhiệm vụ: tạo infra/docker-compose.yml, infra/postgres/Dockerfile (dựa trên postgres:18 + gói PGDG, không dùng
  postgis/postgis vì chỉ có amd64), infra/garage/garage.toml đúng như tài liệu, và script infra/garage/init.sh tự động
  hóa các lệnh khởi tạo Garage (idempotent, tạo khóa troviec-app đọc/ghi và troviec-worker chỉ đọc, in ra access key).
  Phạm vi: infra/, Makefile (target dev-up, dev-down).
  Yêu cầu: không hard-code mật khẩu hay rpc_secret (Garage đọc GARAGE_RPC_SECRET); đọc từ .env ở thư mục gốc;
  kiểm tra image postgres có đủ extension postgis, vector, pg_trgm, unaccent.
  Xong khi: `make dev-up` chạy được postgres (healthy), redis, garage, mailpit; `psql` bằng app_migrator kết nối được;
  `SELECT extname FROM pg_extension` có đủ 4 extension; 3 bucket tồn tại; `docker buildx build --platform
  linux/amd64,linux/arm64 infra/postgres` thành công.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.3 Migrator, bootstrap admin, đơn vị hành chính
- **Phụ thuộc:** T0.2.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/03-database.md (mục Schema và role, Quy tắc migration, Dữ liệu đơn vị hành chính),
  db/migrations/*, db/seed/administrative_units/README.md.
  Nhiệm vụ: tạo console app apps/api/src/TroViec.Migrator (.NET 10) dùng DbUp chạy db/migrations/*.sql theo thứ tự,
  mỗi file một transaction, journal ở app.schema_versions; thêm lệnh `bootstrap-admin` tạo tài khoản admin từ
  Bootstrap__AdminEmail/Bootstrap__AdminPassword (mật khẩu theo chính sách US-101 AC4, băm Argon2id như 07-security.md,
  gán role admin, granter = system, email_verified_at = now) nếu chưa tồn tại; thêm lệnh `import-units <csv>` nạp đơn vị
  hành chính theo README.
  Phạm vi: apps/api/src/TroViec.Migrator, apps/api/TroViec.slnx, Makefile (target migrate).
  Yêu cầu: không sửa nội dung 0001/0002/0003; dùng role app_migrator; log rõ file nào đã chạy.
  Xong khi: `make migrate` trên CSDL rỗng tạo đủ 32 bảng nghiệp vụ (+ bảng journal app.schema_versions của DbUp)
  + 3 materialized view; chạy lại lần 2 không làm gì;
  bootstrap-admin chạy 2 lần không tạo trùng; import-units nạp được file CSV mẫu.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.4 Khung API
- **Phụ thuộc:** T0.3.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/02-architecture.md (mục Backend), docs/03-database.md (mục Kiểu dữ liệu, Lỗi CSDL → mã lỗi),
  docs/04-api.md (mục Quy ước chung, Lỗi, Sức khỏe hệ thống).
  Nhiệm vụ: tạo TroViec.Api (.NET 10 Minimal API) và TroViec.Api.Tests với hạ tầng dùng chung, chưa có feature.
  Phạm vi: apps/api/src/TroViec.Api, apps/api/tests/TroViec.Api.Tests.
  Yêu cầu: NpgsqlDataSource (+ UseVector, Dapper VectorTypeHandler, MatchNamesWithUnderscores); DbEnum helper;
  ProblemDetails theo RFC 9457 có trường code + traceId; PgErrorMapper theo bảng trong 03-database.md;
  ProblemException(code) dùng chung; JSON camelCase, enum dạng snake_case; /health/live, /health/ready;
  OpenAPI (Microsoft.AspNetCore.OpenApi) + Scalar chỉ ở Development; log JSON không chứa PII;
  ForwardedHeaders; cấu hình đọc từ biến môi trường như 08-deployment.md; TreatWarningsAsErrors.
  Test: fixture Testcontainers dùng image infra/postgres, chạy migration thật; test /health/ready; test PgErrorMapper
  với vi phạm uq_users_email.
  Xong khi: `make api` chạy, /openapi/v1.json trả về, `dotnet test` xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.5 Khung worker
- **Phụ thuộc:** T0.3.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (mục Tiến trình, Cấu trúc mã, Hợp đồng hàng đợi, Sổ đăng ký mô hình).
  Nhiệm vụ: tạo project apps/worker (Python 3.12, uv) với khung chạy được nhưng pipeline còn trống.
  Phạm vi: apps/worker/.
  Yêu cầu: config.py (pydantic-settings), db.py (psycopg_pool, role app_worker), streams.py (tạo consumer group nếu
  chưa có, XREADGROUP/XACK/XAUTOCLAIM, dead letter sau 3 lần giao), registry.py (đọc model_versions đang chạy mỗi 60 s),
  __main__.py với lệnh consume | serve-embed | embed-backfill; scan_job.py tạm thời đánh dấu lượt completed với
  assessment giả lập score 0 CHỈ khi WORKER_FAKE_PIPELINE=1; Dockerfile (python:3.12-slim, tesseract-ocr, tesseract-ocr-vie,
  torch bản CPU; build được cho linux/amd64 và linux/arm64); ruff + pytest; Makefile target worker-embed (serve-embed).
  Test: streams với Redis thật (testcontainers); dead letter sau 3 lần lỗi (đặt completed_at); nhận lại lượt đã
  completed/failed thì phát lại scan.completed rồi XACK.
  Xong khi: `make worker` chạy, đọc được việc từ troviec:scan-requested; pytest xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.6 Khung web
- **Phụ thuộc:** T0.4 (cần OpenAPI để sinh client).
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (mục Nguyên tắc, Cấu trúc mã, Phiên đăng nhập, Gọi API, Định dạng).
  Nhiệm vụ: tạo apps/web bằng Next.js 16 (App Router, TypeScript, Tailwind 4, shadcn/ui, pnpm) với layout,
  trang chủ tạm, trang offline, not-found, và lib/api (openapi-typescript + openapi-fetch) sinh từ API đang chạy.
  Phạm vi: apps/web/, Makefile (web, gen-api).
  Yêu cầu: lang="vi"; font hỗ trợ tiếng Việt; lib/format.ts (VND, ngày giờ Asia/Ho_Chi_Minh, khoảng cách) có test Vitest;
  ESLint + typecheck; không có dữ liệu giả cứng trong component.
  Xong khi: `make web` chạy; `pnpm test`, `pnpm lint`, `pnpm build` xanh; `make gen-api` sinh schema.d.ts.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.7 CI
- **Phụ thuộc:** T0.4, T0.5, T0.6.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/08-deployment.md (mục CI/CD), docs/07-security.md (mục Chuỗi cung ứng).
  Nhiệm vụ: tạo .github/workflows/ci.yml với các job db, api, worker, web, contract, images như tài liệu.
  Phạm vi: .github/workflows/.
  Yêu cầu: action ghim theo commit SHA; cache nuget/uv/pnpm; job images build Dockerfile của api, migrator,
  maintenance (chưa có thì tạo project console TroViec.Maintenance tối thiểu + Dockerfile), worker, web cho
  linux/amd64 và linux/arm64 (runner ubuntu-24.04-arm cho phần arm64) và quét Trivy.
  Xong khi: PR thử nghiệm chạy CI xanh toàn bộ.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T0.8 Deploy lên VPS với domain HTTPS — mốc tuần 4
- **Phụ thuộc:** T0.7; đã có VPS và tên miền (mặc định miễn phí: 08-deployment.md, mục Chi phí).
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/08-deployment.md (toàn bộ).
  Nhiệm vụ: tạo infra/docker-compose.prod.yml, infra/caddy/Caddyfile, .github/workflows/deploy.yml, và
  infra/vps/README.md hướng dẫn chuẩn bị VPS (user deploy, UFW — với Oracle mở thêm security list và iptables,
  Docker, thư mục /opt/troviec, .env quyền 600, systemd timer cho maintenance và sao lưu — timer sao lưu để tắt, bật ở T4.4).
  Phạm vi: infra/, .github/workflows/deploy.yml.
  Yêu cầu: chỉ Caddy mở cổng; mem_limit cho mọi service; log rotation; migrator chạy trước khi up api;
  CHƯA bật archive_mode của PostgreSQL (WAL-G có ở T4.4, bật sớm sẽ làm đầy đĩa); maintenance dùng
  ConnectionStrings__Maintenance; Caddy lọc access_token khỏi log; image đa kiến trúc; giữ 5 tag image gần nhất.
  Xong khi: push main → site chạy trên https://<domain> (trang chủ tạm + /health/live), chứng chỉ hợp lệ.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

---

## Giai đoạn 1 — Tài khoản và phân quyền

### T1.1 Đăng ký, xác thực email, đăng nhập, phiên
- **Phụ thuộc:** T0.4. **Story:** US-101, US-102, US-103, US-105.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (E1, BR-13), docs/04-api.md (mục Xác thực, Lỗi, Hạn mức),
  docs/07-security.md (mục Phiên đăng nhập).
  Nhiệm vụ: feature Auth trong TroViec.Api: register, email/verify, email/resend, login, refresh, logout,
  password/forgot, password/reset.
  Phạm vi: Features/Auth, Infrastructure/Auth, Infrastructure/Notifications (IEmailSender + MailKit), test tương ứng.
  Yêu cầu: Argon2id đúng tham số; JWT 15 phút có claim sub, perm, ver; refresh token xoay vòng trong Redis có phát hiện
  dùng lại; cookie đúng thuộc tính; bắt buộc header X-Requested-With cho refresh/logout; khóa tạm sau 5 lần sai;
  register gán role student/owner với granter = app.system_user_id(); student phải dùng tên miền trong
  Auth__StudentEmailDomains; email/resend và password/forgot luôn 204 và có hạn mức theo email + IP;
  đặt lại mật khẩu xóa mọi họ refresh token và tăng uver.
  Test: mọi AC của US-101, 102, 103, 105; refresh token bị dùng lại → thu hồi cả họ; thiếu header → 403.
  Xong khi: dotnet test xanh; luồng đăng ký → mã trong Mailpit → đăng nhập chạy tay được.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T1.2 OTP số điện thoại, `/me`, điều kiện đăng tin
- **Phụ thuộc:** T1.1. **Story:** US-104, US-106, BR-02, BR-12.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (US-104, US-106, BR-02, BR-12, BR-18), docs/04-api.md (mục Tài khoản của tôi).
  Nhiệm vụ: phone/otp, phone/verify, GET/PATCH/DELETE /me (chưa làm phần owner-profile, saved-listings).
  Phạm vi: Features/Me, Features/Auth (phần phone), Infrastructure/Notifications (ISmsSender + ConsoleSmsSender), Domain/PhoneNumber.
  Yêu cầu: chuẩn hóa số về +84 9 số; số chờ xác thực + OTP trong Redis có TTL, giới hạn gửi và nhập sai, chỉ ghi
  users khi xác thực xong (BR-12); ConsoleSmsSender không in số điện thoại và bị chặn ở Production; posting.allowed/missing
  tính đúng BR-02; DELETE /me ẩn danh hóa đúng US-106 AC1 (NULL cả email_verified_at, phone_verified_at, password_hash = '!',
  thu hồi token). Phần tin (AC2) và giấy tờ (AC3) làm ở T3.3/T3.1: để TODO trong code kèm mã task, test viết ở task đó
  (không tạo test skip — AGENTS.md quy tắc 5).
  Test: mọi AC của US-104, US-106 AC1/AC4, các tổ hợp posting.missing.
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T1.3 Phân quyền theo permission và quản trị người dùng
- **Phụ thuộc:** T1.2. **Story:** US-801, US-104 AC6.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (Ma trận phân quyền, US-801), docs/04-api.md (mục Quản trị: users),
  docs/09-testing.md (mục Kiểm thử API: ma trận phân quyền).
  Nhiệm vụ: đăng ký policy cho từng permission code (đọc claim perm); kiểm tra ver với Redis uver:{userId};
  endpoint /admin/users (tìm, gán/thu hồi vai trò, khóa/mở khóa, xác thực số điện thoại thủ công POST /admin/users/{id}/phone);
  test quét EndpointDataSource bắt mọi endpoint phải có policy, RequireAuthorization() (chỉ cần đăng nhập) hoặc
  AllowAnonymous tường minh; khung test ma trận phân quyền đọc kỳ vọng từ một bảng dữ liệu trong test.
  Phạm vi: Infrastructure/Auth, Features/Admin (users), test.
  Yêu cầu: khóa tài khoản → tăng uver và xóa mọi refresh token; đổi vai trò → chỉ tăng uver (refresh phát token quyền mới);
  không tự thu hồi admin, không tự khóa mình; tài khoản system không hiện và không thao tác được.
  Xong khi: dotnet test xanh; token cũ bị từ chối ≤ 30 s sau khi khóa tài khoản.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T1.4 Web: xác thực, phiên, trang tài khoản
- **Phụ thuộc:** T1.2, T0.6.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (mục Bản đồ trang: (auth) và /toi, Phiên đăng nhập, Form), docs/04-api.md (mục Xác thực, /me).
  Nhiệm vụ: trang đăng ký (chọn sinh viên / chủ trọ-nhà tuyển dụng), xác thực email, đăng nhập, quên/đặt lại mật khẩu,
  /toi (thông tin, xác thực số điện thoại, việc còn thiếu để đăng tin, xóa tài khoản); session trong bộ nhớ,
  tự refresh, PermissionGate, useMe(), can().
  Phạm vi: apps/web/app/(auth), apps/web/app/(account)/toi, lib/auth, components/domain liên quan.
  Yêu cầu: lỗi API hiển thị theo code; lỗi validation gán vào từng trường; không lưu token ở localStorage.
  Xong khi: Vitest cho form và lib/auth; đi tay được: đăng ký → mã email → đăng nhập → OTP → /toi báo đủ điều kiện.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

---

## Giai đoạn 2 — Kiểm tra tin

### T2.1 Pipeline tải ảnh và lưu trữ
- **Phụ thuộc:** T0.4. **Quy tắc:** BR-06.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/07-security.md (mục Dữ liệu vào: tệp tải lên), docs/02-architecture.md (bảng thành phần: Garage).
  Nhiệm vụ: Infrastructure/Storage: IObjectStorage (AWSSDK.S3, ForcePathStyle, endpoint Garage), ImagePipeline
  (magic bytes, giới hạn 40 megapixel, xoay EXIF, mã hóa lại WebP chất lượng 82, cạnh dài ≤ 2048, xóa metadata,
  trả sha256 + kích thước), sinh object key phía server; fake IObjectStorage trong bộ nhớ cho test.
  Phạm vi: Infrastructure/Storage, test.
  Test: các ca tệp trong docs/09-testing.md (PNG đổi đuôi, PDF đổi đuôi, ảnh 6 MB, ảnh 12.000×12.000, EXIF GPS bị xóa).
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.2 API lượt kiểm tra, outbox, hạn mức
- **Phụ thuộc:** T2.1, T1.3 (policy theo quyền). **Story:** US-201, US-204, US-205 (phần API).
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (US-201, 204, 205, BR-05, BR-07, BR-20), docs/04-api.md (mục Kiểm tra tin, Danh mục /meta,
  Hạn mức), docs/03-database.md (mục Outbox relay, Kết quả một lượt kiểm tra), docs/05-ai-worker.md (mục Hợp đồng hàng đợi;
  cách tính input_sha256).
  Nhiệm vụ: POST /scans, GET /scans/quota, GET /scans/{scanId}, GET /me/scans, POST /scans/{scanId}/reports; OutboxRelay
  (BackgroundService) đẩy outbox sang Redis Stream đúng hợp đồng và đẩy lại lượt queued quá 5 phút; QuotaService (khách
  5/ngày/IP, đăng nhập 50/ngày, ngày theo giờ Việt Nam); các endpoint /meta (tỉnh, xã, gợi ý xã, tiện nghi, tiêu chí,
  cơ sở đào tạo) vì trang Kiểm tra tin (T2.6) cần chọn phường/xã.
  Phạm vi: Features/Scans, Features/Reports (phần tạo báo cáo), Features/Meta, Infrastructure/Messaging, Infrastructure/RateLimiting, test.
  Yêu cầu: scans + scan_images + outbox_messages ghi trong một transaction; 202 trong ≤ 1 s (phía máy chủ); ảnh vào bucket
  scan-images; chỉ trừ lượt khi tạo được (202); báo cáo trên lượt đặt expires_at = now + 1 năm; mọi endpoint /scans* chỉ
  phục vụ source = quick_check (khác → 404); GET dựng đủ khung response ở 04-api.md (signals, flags, imageMatches, price,
  community — phần worker chưa ghi thì null/rỗng) và trả assessment = null khi chưa xong; evidence đã che PII.
  Test: AC của US-201, 205; outbox khi Redis tắt; quota thứ 6 → 429 có Retry-After; scanId của lượt listing → 404.
  Xong khi: dotnet test xanh; gửi lượt kiểm tra thấy entry trong troviec:scan-requested.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.3 Worker: OCR, định danh, che PII, câu chèn
- **Phụ thuộc:** T0.5, T2.2.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (bước 1–6 của Pipeline), docs/07-security.md (mục Riêng tư và LLM),
  docs/03-database.md (mục Định danh).
  Nhiệm vụ: pipeline/normalize.py, ocr.py (Tesseract vie, tiền xử lý, timeout), identifiers.py (phone, bank_account, url,
  chuẩn hóa, HMAC-SHA256 với IDENTIFIER_HMAC_KEY, hint), pii.py, injection.py; scan_job.py chạy tới bước 6 và lưu
  scan_images.ocr_text, identifiers, scan_identifiers.
  Phạm vi: apps/worker/src/troviec_worker/pipeline, jobs/scan_job.py, tests.
  Yêu cầu: không log giá trị gốc của định danh hay nội dung tin; OCR lỗi một ảnh không làm hỏng lượt; OCR theo ngân sách
  thời gian của lượt; khóa HMAC = byte sau khi giải mã base64 IDENTIFIER_HMAC_KEY, có test vector dùng chung với API;
  câu trích của injection lấy từ văn bản đã che còn dấu, ≤ 300 ký tự.
  Test: mọi ca của identifiers, pii (≥ 30 câu), injection trong docs/09-testing.md.
  Xong khi: pytest xanh; một lượt thật có ảnh chụp màn hình ra được ocr_text và định danh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.4 Worker: tín hiệu chữ, bộ gộp agg-v0, ghi kết quả
- **Phụ thuộc:** T2.3.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (bước 7, 11, 12; Lỗi và mã lỗi; Sổ đăng ký mô hình).
  Nhiệm vụ: llm/base.py, llm/openai_compat.py (httpx, response_format json_schema strict, max_tokens 1500, timeout theo
  ngân sách tối đa 7 s, không retry), llm/fake.py (bật bằng LLM_FAKE=1), llm/prompt.py (prompt-v1 đúng tài liệu, tiêu chí
  nạp từ CSDL theo scope, vô hiệu chuỗi <TIN / </TIN trong văn bản), llm/schema.py (pydantic); kiểm câu trích;
  rules.py (regex không dấu cho 7 tiêu chí seed); text_signal.py (không còn dấu hiệu hợp lệ → text_signal ≤ 0,3);
  aggregate.py với models/aggregator/agg-v0.json (làm tròn floor(x + 0,5)); ghi assessments/flags trong một transaction
  với ON CONFLICT; cache theo input_sha256 chỉ cho quick_check (chép cả scan_identifiers, ocr_text); phát scan.completed
  rồi XACK; ngân sách LLM theo ngày (giờ Việt Nam). Nếu đã chọn nhà cung cấp LLM (README, Việc bạn cần quyết định): thêm
  migration NNNN_register_llm.sql (số kế tiếp sau 0003) — làm ở đây để T2.7 và T4.3 đo được với LLM thật.
  Phạm vi: apps/worker/src/troviec_worker/{llm,pipeline,jobs}, models/aggregator/agg-v0.json, db/migrations (file mới), tests.
  Yêu cầu: không có LLM đang chạy, thiếu API key hoặc LLM_MODEL lệch model_versions.name → chỉ dùng luật;
  nhà cung cấp trả 429 → luật; injection → injectionFloor.
  Test: mọi ca text_signal, aggregate, scan_job trong docs/09-testing.md.
  Xong khi: pytest xanh; lượt kiểm tra thật (với LLM giả) có điểm, mức rủi ro, dấu hiệu, câu trích.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.5 API: nhận kết quả, ScanHub
- **Phụ thuộc:** T2.2, T2.4.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/04-api.md (mục SignalR), docs/02-architecture.md (luồng Kiểm tra tin, Gửi tin đi duyệt — bước 4).
  Nhiệm vụ: ScanCompletedConsumer (consumer group api trên troviec:scan-completed, idempotent, XACK), ScanHub
  (Watch(scanId) chỉ cho lượt quick_check, tối đa 10 nhóm/kết nối, gửi ScanUpdated); với sự kiện có listingId: nếu
  scanId là lượt mới nhất của tin và tin còn ở screening thì chuyển screening → pending_review bằng ListingTransitionService
  (actor system), ngược lại chỉ XACK — nếu ListingTransitionService chưa có thì tạo bản tối thiểu theo
  docs/03-database.md (mục Đổi trạng thái tin) và test riêng.
  Phạm vi: Infrastructure/Messaging, Realtime, Features/Listings/ListingTransitionService.cs, test.
  Xong khi: test tích hợp: sự kiện completed → client SignalR nhận ScanUpdated; sự kiện lặp lại không gây lỗi;
  sự kiện của lượt cũ không đẩy tin đang sàng lọc lượt mới sang pending_review.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.6 Web: trang Kiểm tra tin
- **Phụ thuộc:** T2.5, T1.4. **Story:** US-201, 202, 203, 204.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (mục Nguyên tắc, Hiển thị kết quả kiểm tra, PWA, Thành phần dùng chung),
  docs/04-api.md (mục Kiểm tra tin, SignalR), docs/01-requirements.md (US-201–205).
  Nhiệm vụ: /kiem-tra (form, ảnh, giá, phường/xã qua CommuneCombobox + /meta/communes, số lượt còn lại qua
  GET /scans/quota), /kiem-tra/[scanId] (realtime + poll dự phòng), /toi/kiem-tra; RiskBadge, ScoreGauge, EvidenceList,
  SignalBreakdown, ImageMatchCard, PriceComparison; lib/criteria-tips.ts (có lời khuyên mặc định); manifest.ts +
  public/sw.js có Web Share Target như tài liệu + route dự phòng kiem-tra/chia-se; nút Báo cáo (nhãn loại báo cáo trung tính).
  Phạm vi: apps/web/app/(public)/kiem-tra, app/(account)/toi/kiem-tra, components/domain, lib/realtime, public/sw.js, app/manifest.ts.
  Yêu cầu: câu chữ trung tính và lời nhắc luôn hiển thị (cả ca mức Cao mà không có dấu hiệu chữ); mức rủi ro có chữ +
  biểu tượng + màu; service worker không cache /api; url chia sẻ không bị fetch.
  Xong khi: Vitest cho các thành phần kết quả; đi tay: dán tin đòi cọc → thấy kết quả trong vài giây.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T2.7 Worker: tín hiệu ảnh, giá, cộng đồng — mốc tuần 8
- **Phụ thuộc:** T2.4.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (bước 8, 9, 10), docs/03-database.md (mục Ảnh gần trùng, Mẫu so giá).
  Nhiệm vụ: ml/clip.py (open_clip ViT-B-32 laion2b_s34b_b79k, L2), image_signal.py (pHash có dấu, truy vấn khớp chỉ so
  cùng clip_model_id, ghi phash/clip vào listing_images nếu thiếu, ghi assessment_image_matches, khớp tin của chính người
  đăng → 0), price.py (chỉ trích giá từ chữ khi có từ khóa thuê phòng, so mẫu, MAD, NULL khi < 10 mẫu, ghi price_vnd),
  community_signal.py (đọc identifier_stats, users.created_at của người đăng).
  Phạm vi: apps/worker/src/troviec_worker/{ml,pipeline}, tests.
  Test: ca price, image_signal trong docs/09-testing.md; chuyển đổi pHash có dấu hai chiều; ảnh cắt 20% vẫn khớp.
  Xong khi: pytest xanh; GET /scans trả đủ signals, imageMatches, price, community (khung response đã có từ T2.2);
  đo p95 trên VPS thật (đặc biệt VPS ARM 2 OCPU) — vượt 10 s thì xử lý trước khi sang giai đoạn 3.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

---

## Giai đoạn 3 — Tin đăng, kiểm duyệt, tìm kiếm

### T3.1 Hồ sơ chủ và giấy tờ mã hóa
- **Phụ thuộc:** T1.3, T2.1. **Story:** US-501, 502, 504, US-605 (phần API), BR-10.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (E5, BR-10, BR-11), docs/04-api.md (/me/owner-*, /moderation/owner-*, /files/documents),
  docs/07-security.md (mục Mã hóa và khóa), docs/02-architecture.md (luồng Mở giấy tờ xác minh).
  Nhiệm vụ: /me/owner-profile, /me/owner-documents (tải, liệt kê, xóa); DocumentCipher (AES-256-GCM, key_id);
  SignedUrlService; /moderation/owner-documents/{id}/access (ghi document_access_logs), /moderation/owner-documents/{id}/review,
  /moderation/owner-profiles (danh sách, chi tiết, verify 12 tháng); GET /files/documents/{token}.
  Phạm vi: Features/OwnerProfiles, Features/Moderation (phần hồ sơ), Infrastructure/Crypto, Infrastructure/Storage, test.
  Yêu cầu: consent bắt buộc; trùng sha256 → document.duplicate (trùng tài khoản khác thì log cảnh báo kèm hai userId);
  document.unsupported_type / document.too_large (giới hạn tính trên bản rõ); tệp giải mã trả Cache-Control: no-store;
  admin không có document.view; verify cần ≥ 1 giấy tờ accepted (owner.no_accepted_document); cán bộ không duyệt/xác minh
  hồ sơ của chính mình (moderation.conflict_of_interest); hoàn thiện phần giấy tờ của DELETE /me (US-106 AC3).
  Test: AC E5; token quá 5 phút (TimeProvider giả) / sửa chữ ký → 403; mỗi lần cấp link có dòng nhật ký.
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.2 Tạo và sửa tin, ảnh, tiện nghi
- **Phụ thuộc:** T2.1, T1.3 (policy theo quyền). **Story:** US-301, 302, 303, 307.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (E3: US-301–303, 307), docs/04-api.md (mục Tin đăng — người đăng: tạo, sửa, ảnh),
  docs/03-database.md (bảng listings, room_listings, job_listings, listing_images; Đổi trạng thái tin).
  Nhiệm vụ: POST /listings, PUT /listings/{id}, ảnh (thêm, xóa, sắp thứ tự), GET /me/listings, GET /listings/{id}/history,
  GET /listings/{id} cho chủ tin và cán bộ (mọi trạng thái, kèm status, version; người khác thấy tin chưa công khai → 404) —
  T3.7 cần endpoint này để mở form sửa; phần công khai (contactPhone, reviewSummary…) hoàn thiện ở T3.6.
  Phạm vi: Features/Listings, test.
  Yêu cầu: tạo listings + bảng con + room_amenities + dòng transition (∅ → draft) trong một transaction; validator phản chiếu
  CHECK; chỉ sửa, thêm/xóa/sắp ảnh khi draft; version bắt buộc; chống IDOR (404 với tin chưa công khai, 403 listing.not_owner
  với tin công khai); amenityCodes chỉ nhận tiện nghi đang dùng; ảnh vào bucket listing-images, URL qua S3__PublicMediaBaseUrl;
  sắp thứ tự dùng ràng buộc DEFERRABLE.
  Test: AC US-301–303, 307; IDOR; version_conflict.
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.3 Gửi duyệt, vòng đời, gia hạn, gỡ
- **Phụ thuộc:** T3.2, T2.5. **Story:** US-304, 305, 306, 308; BR-02, BR-03, BR-09.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (Vòng đời tin đăng, US-304–308), docs/02-architecture.md (luồng Gửi tin đi duyệt),
  docs/03-database.md (Đổi trạng thái tin), docs/05-ai-worker.md (định danh: HMAC dùng chung).
  Nhiệm vụ: hoàn thiện ListingTransitionService + ListingLifecycle.IsAllowed (khớp app.listing_transition_allowed bản
  0003, có cạnh expired → draft); submit (tạo scan source listing với requester_id = người đăng, identifier SĐT người đăng,
  outbox), renew, unpublish (verified/rejected/expired → draft), remove; phần tin của DELETE /me (US-106 AC2: mọi tin chưa
  removed → removed, còn tin screening → me.listing_in_screening).
  Phạm vi: Features/Listings, Features/Me, Infrastructure/Crypto (IdentifierHasher), test.
  Yêu cầu: BR-02 kiểm trước khi submit và renew, lỗi trả missing; tin phòng phải có ≥ 1 ảnh; mọi thay đổi trạng thái qua
  service; tin có kháng nghị đang mở → appeal.pending; IdentifierHasher dùng khóa giải mã base64 và test vector chung với worker.
  Test: bảng cạnh hợp lệ/không hợp lệ (đối chiếu với hàm SQL), AC US-304–308, US-106 AC2.
  Xong khi: dotnet test xanh; gửi duyệt → worker chấm → tin sang pending_review.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.4 Kiểm duyệt: hàng đợi, quyết định, ModerationHub
- **Phụ thuộc:** T3.3. **Story:** US-601–604.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (E6: US-601–604), docs/04-api.md (mục Kiểm duyệt: queue, listings, SignalR moderation),
  docs/03-database.md (Hàng đợi duyệt).
  Nhiệm vụ: GET /moderation/queue, GET /moderation/listings/{id}, approve (expires_at + outbox listing.embed), reject, hide, restore;
  ModerationHub (QueueChanged, ListingUpdated).
  Phạm vi: Features/Moderation, Realtime, test.
  Yêu cầu: cán bộ không thao tác trên tin của chính mình (moderation.conflict_of_interest); restore khi có kháng nghị mở
  → appeal.pending.
  Test: hai cán bộ duyệt song song → một 409; reject/hide thiếu lý do → 400; approve tạo outbox embedding; tự duyệt tin mình → 403.
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.5 Báo cáo, kháng nghị, đánh giá, lưu tin
- **Phụ thuộc:** T3.4. **Story:** US-404–406, US-606–609, US-701–702; BR-04, BR-15.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (US-404–406, 606–609, 701–702, BR-04, BR-15), docs/04-api.md (các endpoint tương ứng),
  docs/03-database.md (Lỗi CSDL → mã lỗi: trigger P0001).
  Nhiệm vụ: báo cáo tin, xử lý báo cáo (GET /moderation/reports/{id} kèm nội dung + ảnh của lượt bị báo cáo, confirm có
  tùy chọn ẩn tin trong cùng transaction, dismiss), kháng nghị (tạo, xử lý accept/reject theo bảng vòng đời — accept tin
  rejected thì đặt expires_at mới + outbox embedding), đánh giá (tạo chỉ trên tin công khai/hết hạn, xem, ẩn), lưu/bỏ lưu tin
  (tin không còn công khai hiện "Tin không còn hiển thị"), /moderation/audit/me.
  Phạm vi: Features/Reports, Features/Appeals, Features/Reviews, Features/Me (saved-listings), test.
  Yêu cầu: đổi lỗi trigger thành mã lỗi đúng (uq_appeals_listing_transition_id → appeal.already_used); kiểm tra decider ≠
  người ra quyết định và ≠ người đăng tin ở API trước khi tới CSDL.
  Test: kịch bản báo cáo → ẩn → kháng nghị → chấp nhận; D13–D17 trong docs/09-testing.md ở mức API.
  Xong khi: dotnet test xanh.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.6 Tìm kiếm: bộ lọc, hybrid, dịch vụ embedding
- **Phụ thuộc:** T3.4, T0.5. **Story:** US-401, 402.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (US-401, 402), docs/04-api.md (Tin đăng — công khai; /meta),
  docs/03-database.md (Tìm tin công khai có bộ lọc, Tìm kiếm hybrid), docs/05-ai-worker.md (mục Embedding).
  Nhiệm vụ: worker: ml/text_embed.py (BGE-M3), embed_api.py (POST /internal/embed, X-Internal-Token), embed_job.py
  (listing.embed), lệnh embed-backfill. API: GET /listings (thiếu tọa độ → cơ sở đầu tiên), GET /listings/search (bộ phân
  tích luật tách giá, tiện nghi, loại tin, "khép kín" → selfContained → appliedFilters; gọi embed timeout 800 ms; rơi về
  fulltext; phần câu còn lại rỗng → mode filter; chỉ so embedding cùng model_version_id), hoàn thiện GET /listings/{id}
  công khai (BR-01, BR-14). (Các endpoint /meta đã làm ở T2.2.)
  Phạm vi: apps/worker (ml, embed_api, jobs/embed_job), apps/api Features/Search, Features/Listings, test.
  Test: bộ phân tích câu với ≥ 20 câu mẫu; embed lỗi → mode fulltext; tin hết hạn không xuất hiện.
  Xong khi: test xanh; tìm "phòng khép kín dưới 2,5 triệu có chỗ để xe" trả đúng appliedFilters.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.7 Web: đăng tin, tin của tôi, hồ sơ chủ
- **Phụ thuộc:** T3.3, T3.1, T1.4.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (Bản đồ trang: /toi/tin-cua-toi*, /toi/ho-so-chu; Form; Bản đồ), docs/04-api.md (Tin đăng — người đăng, /me/owner-*).
  Nhiệm vụ: form tin phòng/tin việc (LocationPicker, CommuneCombobox, ImageUploader kéo thả sắp xếp), danh sách tin của tôi
  theo trạng thái với hành động hợp lệ theo vòng đời, trang chi tiết tin của tôi (StatusTimeline, kháng nghị),
  trang hồ sơ chủ + tải giấy tờ (ô đồng ý), VersionConflictDialog.
  Phạm vi: apps/web/app/(account)/toi, components/domain.
  Xong khi: Vitest cho form; đi tay: tạo tin → tải ảnh → gửi duyệt → thấy "Đang sàng lọc" rồi "Chờ duyệt".
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.8 Web: tìm tin, chi tiết tin
- **Phụ thuộc:** T3.6, T3.5.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (Bản đồ trang: /, /tin, /tin/[listingId], /cach-cham-diem; Bản đồ; Chất lượng), docs/04-api.md (Tin đăng — công khai).
  Nhiệm vụ: trang chủ, /tin (câu tự nhiên, bộ lọc, danh sách ↔ bản đồ, chip appliedFilters), /tin/[listingId] (SSR + metadata,
  liên hệ khi đăng nhập, lưu, đánh giá, báo cáo), /cach-cham-diem, /toi/tin-da-luu.
  Phạm vi: apps/web/app/(public), app/(account)/toi/tin-da-luu, components/domain.
  Xong khi: Lighthouse mobile ≥ 90 cho trang chủ và chi tiết tin; tin không công khai trả 404.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.9 Web: bảng kiểm duyệt
- **Phụ thuộc:** T3.4, T3.5, T3.1.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/06-frontend.md (Bản đồ trang: /kiem-duyet*), docs/04-api.md (mục Kiểm duyệt), docs/01-requirements.md (E6).
  Nhiệm vụ: hàng đợi realtime, trang duyệt hai cột (nội dung ↔ kết quả AI, ảnh trùng mở được tin khớp, giá, lịch sử, hồ sơ chủ),
  hộp thoại lý do từ chối/ẩn, báo cáo, kháng nghị, hồ sơ chủ + mở giấy tờ (hộp thoại mục đích), nhật ký của tôi.
  Phạm vi: apps/web/app/(moderation), components/domain.
  Xong khi: đi tay được cả 4 luồng E2E ở docs/09-testing.md phần cán bộ.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.10 Quản trị
- **Phụ thuộc:** T1.3, T3.4. **Story:** US-802–805.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (E8), docs/04-api.md (mục Quản trị), docs/06-frontend.md (/quan-tri/*).
  Nhiệm vụ: API /admin/criteria, /admin/model-versions (+ activate trong một transaction), /admin/stats (đọc reporting.*),
  /admin/audit/*; web các trang /quan-tri tương ứng (hộp thoại xác nhận khi kích hoạt bộ gộp hiển thị ngưỡng).
  Phạm vi: Features/Admin, apps/web/app/(admin).
  Xong khi: test API xanh; kích hoạt agg-v0 ↔ phiên bản khác hoạt động; trang thống kê hiển thị dữ liệu sau khi làm mới view.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T3.11 Job đêm và email thông báo
- **Phụ thuộc:** T3.5. **Story:** US-901, US-902.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/01-requirements.md (US-901, 902), docs/03-database.md (mục Job đêm), docs/08-deployment.md (Tác vụ định kỳ).
  Nhiệm vụ: console app TroViec.Maintenance (role app_maintenance qua ConnectionStrings__Maintenance) chạy 5 việc theo thứ
  tự, mỗi việc độc lập (lỗi một việc không chặn việc khác), xóa tệp S3 trước khi xóa dòng; email thông báo kết quả
  duyệt/ẩn/kháng nghị gửi từ API (IEmailSender ở Infrastructure/Notifications, gọi từ handler của từng feature).
  Phạm vi: apps/api/src/TroViec.Maintenance, Infrastructure/Notifications + Features/Moderation, Features/Appeals (gọi gửi email), test.
  Test: D18, D19 trong docs/09-testing.md; tin bị ẩn quá 7 ngày không kháng nghị → removed.
  Xong khi: test xanh; chạy thử bằng docker compose run maintenance.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

---

## Giai đoạn 4 — Hoàn thiện

### T4.1 Bảo mật
- **Phụ thuộc:** giai đoạn 3 xong.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/07-security.md (toàn bộ), docs/09-testing.md (Kiểm thử bảo mật).
  Nhiệm vụ: rà toàn bộ code theo từng mục kiểm soát trong 07-security.md và lập bảng "đã làm / thiếu / file";
  bổ sung phần thiếu: CSP có nonce cho web, giới hạn body theo endpoint, test tệp tải lên còn thiếu, test token, lọc
  access_token khỏi log, ZAP baseline trong .github/workflows/nightly.yml (chạy trên stack Docker, không có staging).
  Phạm vi: toàn repo (chỉ sửa phần bảo mật).
  Xong khi: bảng rà soát nằm trong docs/security-review.md; ZAP baseline không còn cảnh báo High.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T4.2 Dữ liệu demo và E2E
- **Phụ thuộc:** giai đoạn 3 xong.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/08-deployment.md (Dữ liệu demo, CI/CD), docs/09-testing.md (Đầu cuối), docs/05-ai-worker.md (Sổ đăng ký mô hình).
  Nhiệm vụ: db/seed/demo (idempotent: tài khoản demo đã xác thực cho mọi vai trò, 20 tin phòng, 10 tin việc, 5 tin lừa mẫu đã
  ẩn danh, bộ ảnh dùng lại) và 4 kịch bản Playwright chạy trên docker compose với LLM giả (LLM_FAKE=1; script E2E tự đăng ký
  dòng llm fake/deterministic — không đưa vào migration hay seed demo).
  Phạm vi: db/seed/demo, apps/web/e2e, .github/workflows/nightly.yml (job e2e hằng đêm).
  Xong khi: 4 kịch bản xanh liên tiếp 3 lần.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T4.3 Kiểm thử tải và tối ưu
- **Phụ thuộc:** T0.8, giai đoạn 3 xong; LLM đã đăng ký ở T2.4 nếu dùng.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/09-testing.md (Kiểm thử tải), docs/03-database.md (Truy vấn mẫu).
  Nhiệm vụ: viết tests/load/browse.js, search.js, quick-check.js (k6) với ngưỡng như tài liệu — quick-check.js dùng
  constant-arrival-rate 6 lượt/phút × 5 phút, đo p95 của completedAt − createdAt; chạy, đọc EXPLAIN ANALYZE các truy vấn
  chậm nhất, tối ưu (chỉ mục mới đi qua migration mới, có lý do).
  Xong khi: đạt ngưỡng; kết quả ghi vào research/results/e6-<ngày>.json.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T4.4 Sao lưu, khôi phục, giám sát
- **Phụ thuộc:** T0.8; đã tạo bucket Backblaze B2 (08-deployment.md, mục Chi phí).
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/08-deployment.md (Chi phí, Tác vụ định kỳ, Sao lưu và khôi phục, Giám sát).
  Nhiệm vụ: cài WAL-G (đúng kiến trúc amd64/arm64) trong image postgres, bật archive_mode (lần đầu, sau khi WALG_* đã cấu
  hình), bật các systemd timer sao lưu, script rclone, script cảnh báo đĩa, monitor UptimeRobot + Telegram bot,
  runbook khôi phục infra/vps/RESTORE.md; diễn tập khôi phục một lần.
  Xong khi: biên bản diễn tập (RPO, RTO thực tế) trong research/results/restore-<ngày>.md.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T4.5 Dữ liệu nghiên cứu, PhoBERT, agg-v1 — chốt tính năng tuần 11
- **Phụ thuộc:** T2.7; đã gán nhãn xong 300–500 tin.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (bước 7 PhoBERT, bước 11 agg-v1, Sổ đăng ký mô hình, Đánh giá), docs/03-database.md (Schema và role).
  Nhiệm vụ: migration research (research.samples, sample_labels, sample_flags; GRANT cho role app_research — role đã do
  db/init/00_init.sh tạo, migration không tạo role được vì app_migrator không có CREATEROLE); script nhập dữ liệu gán nhãn
  từ CSV; notebook/script huấn luyện PhoBERT (tách từ underthesea, chia 60/20/20 theo group_key, tập test ≥ 100) và agg-v1
  (logistic, chọn ngưỡng Precision ≥ 0,80, báo cáo kèm khoảng tin cậy 95%); ml/phobert.py; migration đăng ký text_classifier
  và aggregator agg-v1 (số kế tiếp; LLM đã đăng ký ở T2.4).
  Phạm vi: research/, apps/worker/src/troviec_worker/ml, apps/worker/models, db/migrations (file mới).
  Xong khi: kích hoạt agg-v1 trên giao diện admin, lượt kiểm tra mới dùng agg-v1; kết quả validation lưu trong research/results.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

Việc không cần code nhưng phải chạy song song từ tuần 2: thu thập tin lừa từ cảnh báo của công an, báo chí, nhà trường
và tin sinh viên gửi (có đồng ý); viết hướng dẫn gán nhãn (`research/labeling-guide.md`); gán nhãn 300–500 tin; người
thứ hai gán lại 20%, kappa ≥ 0,7.

---

## Giai đoạn 5 — Thử nghiệm và báo cáo

### T5.1 Thí nghiệm E1–E5
- **Phụ thuộc:** T4.5.
- **Prompt:**
  ```text
  Đọc AGENTS.md, docs/05-ai-worker.md (mục Đánh giá), docs/00-overview.md (Chỉ tiêu).
  Nhiệm vụ: research/eval/e1_text.py … e5_search.py chạy offline trên tập test, ghi JSON kết quả + biểu đồ PNG,
  và research/results/README.md tóm tắt bảng số liệu cho chương 5.
  Xong khi: chạy lại được bằng một lệnh; số liệu kèm phiên bản mô hình và commit Git.
  Trước khi viết code, trình bày kế hoạch ngắn và chờ tôi duyệt.
  ```

### T5.2 Thử nghiệm với sinh viên (E7)
Không cần code mới: mở hệ thống cho ≥ 30 sinh viên trong 2–4 tuần, thu số liệu từ trang thống kê, gửi khảo sát
SUS 10 câu, phỏng vấn ngắn 5–8 người. Tắt dữ liệu demo trước khi mở. Xuất số liệu thống kê (`reporting.*`) **hằng tuần**
ra `research/results/`: lượt kiểm tra nhanh bị xóa sau 30 ngày nên view thống kê của các ngày cũ giảm dần.

### T5.3 Chuẩn bị demo
QR trên slide trỏ tới `/kiem-tra`; 5 tin mẫu cho kịch bản 7 phút trong đề cương; video demo dự phòng; bản
`docker compose` chạy được trên laptop phòng khi VPS sự cố.

---

## Giai đoạn 6 — Quản lý trọ (sau bảo vệ)

Không bắt đầu trước buổi bảo vệ. Sáu task T6.1–T6.6 (phụ thuộc, nội dung, tiêu chí xong) nằm ở
[12-rental.md](12-rental.md#task). Prompt viết theo mẫu chung ở đầu file này, phần "Đọc" trỏ tới các mục của
12-rental.md mà task cần.
