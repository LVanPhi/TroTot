# Rà soát bảo mật (T4.1)

Đối chiếu từng kiểm soát trong [07-security.md](07-security.md) với code ngày 22/11/2026.
**Đã làm** = có code và test tự động; **Một phần** = có code, thiếu kiểm chứng hoặc thiếu một vế;
**Thiếu** = chưa làm (ghi vào hạn chế của đồ án nếu không bổ sung).

## Phiên đăng nhập

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| Argon2id OWASP (19 MiB, t=2, p=1), PHC, so sánh thời gian hằng | Đã làm | `TroViec.Security/PasswordHasher.cs` | `PasswordTests` |
| Mật khẩu ≥ 10 ký tự, không thuộc 10.000 mật khẩu phổ biến | Đã làm | `TroViec.Security/PasswordPolicy.cs` | `Register_WeakPassword_*` |
| JWT HS256 15 phút, kiểm `iss`/`aud`/`exp`, khóa ≥ 32 byte | Đã làm | `Infrastructure/Auth/TokenService.cs`, `AuthSetup.cs` | `SecurityTests.AccessToken_*` (sửa chữ ký, khóa lạ, `alg: none`, hết hạn) — **thêm ở T4.1** |
| `uver` thu hồi token ngay khi khóa/đổi vai trò/đặt lại mật khẩu/xóa tài khoản | Đã làm | `UserVersionStore.cs`, `AuthSetup.RejectStaleVersionAsync` | `Reset_ValidToken_*`, test admin |
| Refresh token xoay vòng, bia mộ, phát hiện dùng lại → thu hồi cả họ | Đã làm | `RefreshTokenStore.cs` | `Refresh_OldTokenReused_*` |
| Cookie `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth` | Đã làm | `AuthEndpoints` | `Login_VerifiedStudent_*` |
| CSRF: `X-Requested-With` trên `/auth/refresh`, `/auth/logout`; CORS đúng origin | Đã làm | `Features/Auth`, `ServiceSetup.AddCorsForSite` | `CookieEndpoints_WithoutCsrfHeader_*`, `CorsTests` |
| Khóa tạm 5 lần sai / 15 phút theo tài khoản và IP | Đã làm | `Throttles.cs` | `Login_FiveWrongPasswords_*` |
| Mã email/OTP 6 số, băm, TTL, 5 lần sai; số chờ chỉ trong Redis (BR-12) | Đã làm | `OneTimeCodes.cs`, `Features/Auth/Phone*` | `Verify_FiveWrongCodes_*`, test OTP |
| `Sms__Provider=console` bị từ chối ở Production | Đã làm | `Program.cs` (resolve `ISmsSender` lúc khởi động) | test cấu hình SMS |

## Phân quyền

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| Policy theo mã quyền, mọi endpoint khai báo tường minh | Đã làm | `Permissions.cs`, từng `Map*` | `AuthorizationMatrixTests.Matrix_CoversEveryApiEndpoint` (quét `EndpointDataSource`) |
| Ma trận endpoint × vai trò | Đã làm | — | `AuthorizationMatrixTests` (≈ 90 endpoint × 6 vai trò) |
| Chống IDOR: 404 với tài nguyên chưa công khai, 403 `listing.not_owner` với tin công khai | Đã làm | `Features/Listings/ListingAccess.cs`, giấy tờ, kháng nghị | `ListingTests.OtherUser_*`, `OwnerDocumentTests.Delete_*` |
| Tách nhiệm vụ BR-19 (tin, hồ sơ, giấy tờ, báo cáo, kháng nghị, đánh giá) | Đã làm | `ListingTransitionService`, các handler kiểm duyệt | `*_ConflictOfInterest`, `ReportAppealReviewTests` |
| Người xử lý kháng nghị ≠ người ra quyết định (API + trigger) | Đã làm | `AppealEndpoints`, trigger `check_appeal` | `Report_Hide_Appeal_Accept_EndToEnd` (D16) |
| Admin không có `document.view` | Đã làm | seed `0002` | `Access_TamperedTokenIs403_AdminLacksDocumentView_*` |

## Dữ liệu vào

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| FluentValidation mọi request | Đã làm | `Infrastructure/Problems/ValidationFilter.cs` | test từng feature |
| Giới hạn body: JSON 1 MB, ảnh kiểm tra 30 MB, ảnh tin ~5 MB, giấy tờ ~10 MB | Đã làm — **thêm ở T4.1** | `Infrastructure/Problems/RequestLimits.cs` (mặc định 1 MB, endpoint tải lên khai báo `RequestSizeLimit`) | `SecurityTests.JsonBodyOverOneMegabyte_Is413*`, `UploadEndpoint_KeepsItsOwnLargerLimit` |
| SQL tham số hóa; cột sắp xếp từ whitelist | Đã làm | Dapper ở mọi feature; `SearchEndpoints.FilteredAsync` | `List_FiltersAndSort_*` (`sort=title` → 400) |
| Magic bytes, bỏ qua đuôi file và `Content-Type` | Đã làm | `ImagePipeline.cs`, `OwnerProfileEndpoints.DetectMime` | `Process_PngNamedJpg_*`, `Process_PdfNamedJpg_*`, `Upload_RequiresConsent*` |
| Từ chối ảnh > 40 MP trước khi giải mã; > 5 MB dừng đọc | Đã làm | `ImagePipeline.cs` | `Process_12000x12000Pixels_*` (144 MP), `Process_SixMegabytes_*` |
| Mã hóa lại WebP, xoay theo EXIF, xóa metadata | Đã làm | `ImagePipeline.cs` | `Process_JpegWithExifGps_*`, `Process_ExifOrientation*` |
| Tệp polyglot (ảnh hợp lệ + mã HTML/PHP phía sau) không mang payload ra ngoài | Đã làm — **test thêm ở T4.1** | `ImagePipeline.cs` (mã hóa lại) | `SecurityTests.PolyglotImage_*` |
| Object key do server sinh | Đã làm | `ObjectKeys` | `Keys_AreServerGenerated` |
| Ảnh tin phục vụ với `nosniff`, `Content-Type` do server đặt | Đã làm | Caddy `header`, Garage website | cấu hình (kiểm tay) |
| Không fetch URL người dùng gửi (SSRF), kể cả Web Share Target | Đã làm | không có mã gọi URL; `lib/scans/form.ts sharedText` | `scan-view.test.ts` (url chỉ là chữ) |
| Dịch vụ embedding chỉ nội bộ + `X-Internal-Token` | Đã làm | `embed_api.py`, compose (không publish cổng) | `test_embed_wrong_token_returns_401` |
| Khóa Garage tối thiểu: `troviec-app` đọc/ghi, `troviec-worker` chỉ đọc 2 bucket | Đã làm | `infra/garage/init.sh` | kiểm tay |

## Đầu ra và trình duyệt

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| Không `dangerouslySetInnerHTML` với nội dung người dùng | Đã làm | toàn bộ `apps/web` (không có lần dùng nào) | grep trong rà soát này |
| CSP có nonce mỗi request | Đã làm — **thêm ở T4.1** | `apps/web/proxy.ts`, `lib/security/csp.ts` | `tests/csp.test.ts` |
| Header Caddy: HSTS, nosniff, Referrer-Policy, Permissions-Policy, X-Frame-Options, xóa `Server` | Đã làm | `infra/caddy/Caddyfile` | ZAP baseline hằng đêm |

**Quyết định ở T4.1 (đã ghi vào 07-security.md):**

- CSP dùng nonce thì mọi trang phải render theo request: tắt `cacheComponents` (partial prerendering) trong
  `next.config.ts`, layout gốc đọc `headers()`. Đổi lại mất static shell; với quy mô đồ án, mọi trang vốn đã lấy dữ
  liệu theo người dùng nên chi phí chấp nhận được.
- `style-src` cho phép `'unsafe-inline'`: sonner và maplibre chèn `<style>`/`style=""` lúc chạy, không mang nonce.
  `script-src` vẫn chặt (`'nonce-…' 'strict-dynamic'`), đây mới là lớp chặn XSS; CSS nội tuyến không thực thi mã.

## Nhật ký và giám sát

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| Log JSON có `traceId`, chỉ log id | Đã làm | `Program.cs` (JSON console), mọi `Log*` dùng id | rà mã |
| Không ghi query `access_token` vào log API | Đã làm — **gia cố ở T4.1** | `Program.cs`: `AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", Warning)` trong code (không phụ thuộc appsettings) | rà mã |
| Caddy lọc `access_token` khỏi log, giữ 14 ngày | Đã làm | `Caddyfile` (`format filter … delete access_token`) | kiểm tay khi deploy |
| Nhật ký nghiệp vụ chỉ-thêm | Đã làm | `listing_transitions`, `document_access_logs` (REVOKE ở 0001) | test CSDL |
| Cảnh báo `refresh_reused`, 401/429 tăng đột biến | **Thiếu** | — | làm ở T4.4 (giám sát) |

## Lưu giữ, chuỗi cung ứng, bí mật

| Kiểm soát | Trạng thái | Nơi cài đặt | Kiểm chứng |
|---|---|---|---|
| Xóa lượt kiểm tra 30 ngày (1 năm nếu có báo cáo), xóa tệp trước dòng | Đã làm | `TroViec.Maintenance/NightlyJobs.cs` | `NightlyJobsTests` (D18) |
| Giấy tờ xóa tệp, giữ `erased_at`; xóa tài khoản ẩn danh hóa | Đã làm | `OwnerProfileEndpoints.EraseAsync`, `MeEndpoints.DeleteAsync` | `DeleteAccount_*` |
| Lockfile, Dependabot, Actions ghim SHA | Đã làm | lockfiles, `.github/dependabot.yml` | CI |
| `dotnet list --vulnerable`, `pnpm audit`, `pip-audit`, Trivy chặn High | Đã làm | `.github/workflows/ci.yml` | CI |
| ZAP baseline hằng đêm trên stack Docker, chặn High | Đã làm — **thêm ở T4.1** | `.github/workflows/nightly.yml` | chạy lần đầu sau khi merge (lịch hoặc `workflow_dispatch`) |
| Bí mật chỉ ở `.env`/Secrets, mỗi role CSDL một mật khẩu | Đã làm | `.env.example`, `db/init/00_init.sh` | rà mã |

## Còn thiếu / hạn chế

- Cảnh báo giám sát (refresh_reused, 401/429) — T4.4.
- ZAP baseline chưa chạy lần nào: workflow chỉ chạy trên nhánh mặc định (lịch) hoặc bấm tay sau khi merge.
- Các hạn chế đã biết ở 07-security.md (bộ đếm báo cáo không trừ, nhật ký thao tác quản trị…) giữ nguyên.
