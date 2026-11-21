# 07 — Bảo mật và quyền riêng tư

Rủi ro lớn nhất về pháp lý là **gắn oan người ngay thẳng** và **lộ dữ liệu cá nhân** (số điện thoại,
giấy tờ, ảnh chụp tin). Mọi kiểm soát dưới đây là bắt buộc; mục nào chưa làm phải ghi rõ trong
báo cáo đồ án là hạn chế.

## Tài sản và mối đe dọa chính

| Tài sản | Mối đe dọa | Kiểm soát chính |
|---|---|---|
| Giấy tờ xác minh (CCCD, giấy phép) | Lộ qua link, qua storage, qua backup | Mã hóa AES-256-GCM ở tầng ứng dụng; token xem 5 phút; nhật ký mỗi lần mở; admin không có quyền xem |
| Số điện thoại, số tài khoản bên thứ ba | Dò ngược từ CSDL bị lộ | Chỉ mục định danh (`identifiers`) chỉ lưu HMAC-SHA256 với khóa bí mật nằm ngoài CSDL. Bản rõ chỉ còn trong nội dung gốc của lượt kiểm tra (`scans.input_text`, `scan_images.ocr_text`) và bị xóa cùng lượt sau 30 ngày |
| Ảnh và chữ người dùng gửi để kiểm tra | Người khác xem kết quả; lưu quá lâu | `scanId` UUID không đoán được; không có endpoint trả lại ảnh/chữ gốc (trừ cán bộ xử lý báo cáo trên lượt đó); tự xóa sau 30 ngày |
| Tài khoản cán bộ | Chiếm tài khoản để duyệt tin lừa; cán bộ tự duyệt tin của mình | Mật khẩu mạnh, khóa tạm sau 5 lần sai, phiên ngắn, thu hồi token khi khóa; tách nhiệm vụ BR-19 |
| Quyết định kiểm duyệt | Chối bỏ, sửa lịch sử | Bảng chỉ-thêm, `REVOKE UPDATE, DELETE` ở CSDL |
| Pipeline AI | Câu chèn điều khiển LLM, rò dữ liệu ra nhà cung cấp | Che PII bắt buộc; tách dữ liệu khỏi lệnh; câu trích phải khớp nguyên văn; luật độc lập |
| Hạ tầng | Cạn tài nguyên, chi phí LLM tăng vọt | Hạn mức theo IP/tài khoản; ngân sách gọi LLM/ngày; timeout mọi lời gọi |

## Danh mục kiểm soát

### Phiên đăng nhập

- **Mật khẩu**: Argon2id (`Konscious.Security.Cryptography.Argon2`), tham số tối thiểu theo OWASP:
  bộ nhớ 19 MiB, 2 vòng lặp, song song 1; muối 16 byte ngẫu nhiên; lưu chuỗi PHC
  `$argon2id$v=19$m=19456,t=2,p=1$<salt>$<hash>`; so sánh bằng `CryptographicOperations.FixedTimeEquals`.
  Mật khẩu ≥ 10 ký tự, không nằm trong danh sách 10.000 mật khẩu phổ biến (file nhúng trong API).
- **Access token**: JWT HS256, khóa `Jwt__SigningKey` ≥ 32 byte ngẫu nhiên, kiểm `iss`, `aud`, `exp`
  (lệch giờ tối đa 30 s), sống 15 phút. Claim `ver` so với Redis `uver:{userId}` (tiến trình API vừa tăng `uver` từ chối
  token cũ ngay; tiến trình khác chậm tối đa 30 s do cache); khóa tài khoản, đổi
  vai trò, đặt lại mật khẩu hoặc xóa tài khoản thì tăng `uver` để mọi token cũ bị từ chối ngay (cache 30 s ở API).
  Đổi vai trò không xóa refresh token: lần refresh kế tiếp phát token mang quyền mới.
- **Refresh token** (Redis):
  - Token = 32 byte ngẫu nhiên, base64url. Lưu `rt:{sha256(token)}` → `{userId, familyId}` với TTL 30 ngày;
    tập `rtf:{familyId}` chứa các băm thuộc họ.
  - Làm mới: `GETDEL rt:{hash}` (nguyên tử). Có → tạo token mới cùng họ, ghi bia mộ `rtu:{hash}` (TTL 30 ngày).
    Không có nhưng có bia mộ → **token bị dùng lại**: xóa cả họ, trả `auth.refresh_reused`.
  - Tập `rtusr:{userId}` chứa các `familyId` của người dùng, để "xóa mọi họ" không phải quét Redis.
  - Đăng xuất: xóa cả họ. Đặt lại mật khẩu, khóa tài khoản, xóa tài khoản: xóa mọi họ của người dùng.
- **Cookie** `tv_refresh`: `HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`.
- **CSRF**: chỉ hai endpoint dùng cookie (`/auth/refresh`, `/auth/logout`); ngoài SameSite=Strict còn bắt
  buộc header `X-Requested-With: troviec` (header tùy biến buộc trình duyệt preflight). CORS chỉ cho phép
  đúng origin của site.
- **Khóa tạm**: 5 lần sai mật khẩu trong 15 phút → khóa đăng nhập 15 phút; đếm theo tài khoản và theo IP.
- **Mã email / OTP**: 6 chữ số từ `RandomNumberGenerator`; lưu băm trong Redis với TTL; tối đa 5 lần nhập
  sai; so sánh thời gian hằng. Số điện thoại chờ xác thực nằm cùng OTP trong Redis, chỉ ghi vào `users` khi
  xác thực xong (BR-12). `ConsoleSmsSender` chỉ cho dev: API từ chối khởi động nếu `Sms__Provider=console` ở
  môi trường Production; log in OTP kèm `userId`, không in số điện thoại.

### Phân quyền

Mỗi mã quyền là một policy cùng tên (`Infrastructure/Auth/Permissions.cs`) yêu cầu claim `perm` tương ứng. Test quét
`EndpointDataSource` bắt mọi endpoint khai báo quyền tường minh, và ma trận phân quyền trong
`tests/TroViec.Api.Tests/Admin/AuthorizationMatrixTests.cs` phải có một dòng cho mỗi endpoint.

- Mỗi endpoint khai báo policy theo **quyền** (`RequireAuthorization("listing.moderate")`); policy đọc claim
  `perm`. Endpoint chỉ đụng dữ liệu của chính người gọi (`/me`, `/auth/phone/*`) dùng `RequireAuthorization()`
  không kèm quyền; endpoint công khai dùng `AllowAnonymous()` tường minh. Không endpoint nào thiếu khai báo —
  có test quét toàn bộ route để chặn trường hợp quên.
- **Kiểm tra quyền sở hữu** (chống IDOR) ở mọi thao tác trên tài nguyên của người dùng: tin
  (`poster_id = sub`), giấy tờ (`owner_id = sub`), kháng nghị (qua tin), lượt kiểm tra trong lịch sử
  (`requester_id = sub`). Trả `404` thay vì `403` khi người khác truy cập tin chưa công khai, để không lộ sự tồn tại;
  `403 listing.not_owner` chỉ dùng khi tin đang công khai (ai cũng biết tin tồn tại).
- Quy tắc tách nhiệm vụ: người xử lý kháng nghị khác người ra quyết định (trigger CSDL + kiểm tra ở API);
  cán bộ không xử lý tin, hồ sơ, báo cáo, kháng nghị liên quan tới chính mình (BR-19, kiểm ở API).
- Test ma trận phân quyền tự động: mỗi endpoint × mỗi vai trò → mã trạng thái mong đợi (09-testing.md).

### Dữ liệu vào

- FluentValidation cho mọi request; giới hạn kích thước body: JSON 1 MB, multipart ảnh 30 MB,
  giấy tờ 11 MB (cấu hình theo endpoint).
- SQL luôn tham số hóa (Dapper); tên cột sắp xếp lấy từ whitelist.
- **Tệp tải lên**:
  1. Kiểm magic bytes (JPEG `FF D8 FF`, PNG `89 50 4E 47`, WebP `RIFF….WEBP`, PDF `%PDF-`), bỏ qua đuôi file
     và `Content-Type` client gửi.
  2. Giải mã ảnh bằng ImageSharp với giới hạn 40 megapixel (chống decompression bomb); lỗi → `image.unsupported_type`.
  3. Mã hóa lại sang WebP chất lượng 82, cạnh dài ≤ 2.048 px, xoay theo EXIF rồi **xóa toàn bộ metadata**.
  4. Object key do server sinh, không dùng tên file người dùng: ảnh tin `{listingPublicId}/{uuid}.webp`,
     ảnh kiểm tra `{scanPublicId}/{imageNo}.webp`, giấy tờ `{ownerUserId}/{uuid}.bin` (bản mã).
  5. Ảnh tin công khai phục vụ với `X-Content-Type-Options: nosniff` và `Content-Type` do server đặt.
- **Không fetch URL do người dùng gửi** trong MVP (chống SSRF), kể cả `url` chia sẻ qua Web Share Target. Dịch vụ
  embedding chỉ nghe trong mạng nội bộ Docker và kiểm `X-Internal-Token`.
- Object storage theo đặc quyền tối thiểu: khóa Garage `troviec-app` (api, maintenance) đọc/ghi 3 bucket; khóa
  `troviec-worker` chỉ **đọc** `scan-images`, `listing-images` (worker không ghi S3, không thấy bucket giấy tờ).

### Đầu ra và trình duyệt

- React tự escape; cấm `dangerouslySetInnerHTML` với nội dung người dùng.
- Content Security Policy có nonce cho trang HTML (theo hướng dẫn CSP của Next.js), tối thiểu:
  `default-src 'self'; img-src 'self' data: blob: <nguồn tile bản đồ>; connect-src 'self' wss://{DOMAIN}
  <nguồn tile>; frame-ancestors 'none'; base-uri 'self'; form-action 'self'`. Ở dev thêm
  `http://localhost:8080 ws://localhost:8080` vào `connect-src` và `http://listing-images.web.garage.localhost:3902`
  vào `img-src` (API và ảnh khác origin).
- Header từ Caddy: `Strict-Transport-Security: max-age=31536000; includeSubDomains`,
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: strict-origin-when-cross-origin`,
  `Permissions-Policy: camera=(), microphone=(), geolocation=(self)`, `X-Frame-Options: DENY`, xóa `Server`.

### Mã hóa và khóa

| Dữ liệu | Cách bảo vệ | Khóa |
|---|---|---|
| Mọi kết nối | TLS (Caddy, Let's Encrypt); kết nối nội bộ trong mạng Docker riêng | — |
| Giấy tờ | AES-256-GCM, nonce 96 bit ngẫu nhiên mỗi tệp, lưu `nonce ‖ ciphertext ‖ tag`; `owner_documents.key_id` ghi khóa đã dùng | `Crypto__DocumentKeys` (JSON `{"k1": "<base64 32 byte>"}`) + `Crypto__ActiveDocumentKeyId` |
| Định danh bên thứ ba | HMAC-SHA256 trên giá trị chuẩn hóa. Không dùng salt ngẫu nhiên (sẽ không đối chiếu được giữa các tin), không dùng SHA-256 thuần (số di động Việt Nam chỉ cỡ một tỷ khả năng, dò ngược rất nhanh) | `Crypto__IdentifierHmacKey` (dùng chung API và worker; cả hai giải mã base64 thành 32 byte rồi mới dùng làm khóa — 05-ai-worker.md bước 4) |
| Link xem giấy tờ | Token `base64url(docId ‖ viewerId ‖ exp ‖ HMAC)`, hết hạn 5 phút | `Crypto__SignedUrlKey` |
| Bản sao lưu | Mã hóa phía client trước khi đẩy ra nơi lưu trữ ngoài (WAL-G libsodium) | `WALG_LIBSODIUM_KEY` |
| Ổ đĩa VPS | Mã hóa ổ đĩa nếu nhà cung cấp hỗ trợ | — |

Xoay khóa giấy tờ: thêm `k2`, đổi khóa đang dùng sang `k2`; tệp cũ vẫn giải mã bằng `k1` theo `key_id`.
**Không bao giờ** commit khóa; khóa sinh bằng `openssl rand -base64 32`.

### Riêng tư và LLM

- Văn bản chỉ được gửi ra LLM bên ngoài **sau** lớp che PII (05-ai-worker.md, bước 5); lớp này có bộ
  test ≥ 30 câu thật.
- Không gửi ảnh ra LLM bên ngoài trong MVP.
- Chọn chế độ của nhà cung cấp không dùng dữ liệu để huấn luyện và lưu ít nhất có thể (nếu có). Gói miễn phí
  cũng phải đạt điều này: **không** dùng gói không trả phí của Gemini API (điều khoản 23/03/2026 cho phép Google
  dùng dữ liệu để cải thiện sản phẩm và cho người đọc); nếu dùng Groq gói Free thì bật Zero Data Retention
  (kiểm lại điều khoản khi đăng ký — 08-deployment.md, mục Chi phí).
- Gọi LLM có máy chủ ở nước ngoài là chuyển dữ liệu xuyên biên giới theo Luật Bảo vệ dữ liệu cá nhân
  số 91/2025/QH15: lập hồ sơ đánh giá tác động trước khi vận hành công khai (đề cương, mục Bảo mật, pháp lý).
  Điều này áp dụng cả khi **VPS hoặc nơi lưu sao lưu** đặt ngoài Việt Nam (ví dụ VPS miễn phí ở Singapore,
  Backblaze B2): ghi các bên này vào cùng hồ sơ. PhoBERT tự host là phương án không gửi dữ liệu ra ngoài.
- Xin đồng ý trước khi tải giấy tờ (`consent_at`); trang chính sách quyền riêng tư nêu rõ dữ liệu nào được
  lưu, bao lâu, và cách yêu cầu xóa.

### Nhật ký và giám sát

- Log JSON có cấu trúc kèm `traceId`; **không log** mật khẩu, token, cookie, số điện thoại, email, nội dung
  tin, câu trả lời LLM — chỉ log id (`userId`, `scanId`).
- Middleware xóa header `Authorization`, `Cookie` khỏi log; log Caddy giữ 14 ngày.
- SignalR truyền token qua query `access_token`: log request của ASP.NET Core ghi đường dẫn **không kèm query**
  trên `/hubs/*`, và Caddy lọc tham số này bằng bộ mã hóa `filter` (`request>uri query { delete access_token }`,
  08-deployment.md). Nếu không, JWT nằm trong log 14 ngày.
- Nhật ký nghiệp vụ trong CSDL (không phải log ứng dụng): `listing_transitions`, `document_access_logs`.
- Cảnh báo: nhiều `auth.refresh_reused`, tăng đột biến `401`/`429`, lượt mở giấy tờ bất thường của một cán bộ.

### Lưu giữ và xóa dữ liệu

| Dữ liệu | Thời gian giữ | Cách xóa |
|---|---|---|
| Lượt kiểm tra nhanh + ảnh | 30 ngày; có báo cáo thì 1 năm | Job đêm (03-database.md) |
| Giấy tờ | Đến khi chủ hồ sơ xóa hoặc xóa tài khoản | Xóa tệp, giữ dòng với `erased_at` |
| Tài khoản | Đến khi yêu cầu xóa | Ẩn danh hóa (US-106) |
| Log ứng dụng, log Caddy | 14 ngày | Xoay vòng log |
| Bản sao lưu | 7 bản hằng ngày + 4 bản hằng tuần | Chính sách lưu giữ của WAL-G; dữ liệu đã xóa/ẩn danh hóa còn trong bản sao lưu tối đa 4 tuần (nêu trong chính sách riêng tư) |

## Hạn chế đã biết

Ghi vào báo cáo đồ án như giới hạn của MVP:

- **Bộ đếm báo cáo không trừ lại.** Báo cáo đã xác nhận cộng `identifier_stats` (bộ đếm lịch sử); nếu sau đó
  kháng nghị được chấp nhận vì quyết định ẩn sai, số điện thoại vẫn giữ số báo cáo → tín hiệu cộng đồng của các
  tin sau cao hơn thực tế. Cán bộ cần thận trọng khi xác nhận báo cáo; cải tiến: trigger trừ bộ đếm khi báo cáo
  được đổi sang `dismissed` sau kháng nghị.
- **Xóa ảnh khi `draft` xóa luôn bằng chứng khớp ảnh** (`assessment_image_matches` CASCADE theo ảnh). Người
  đăng có thể gỡ về nháp rồi xóa ảnh đã bị phát hiện dùng lại; cải tiến: xóa mềm ảnh đã qua sàng lọc.
- **Thao tác quản trị không có nhật ký riêng** (khóa/mở khóa, gán/thu hồi vai trò, kích hoạt mô hình, xác thực
  số thủ công): chỉ có log ứng dụng 14 ngày; `user_roles.granter_id` mất khi thu hồi vai trò.
- **Link xem giấy tờ** mở lại được nhiều lần trong 5 phút và không gắn với phiên của người xem; nhật ký ghi theo
  lần cấp link (BR-10).
- **Ảnh tin công khai** (website mode) vẫn tải được bằng URL cũ sau khi tin bị ẩn/gỡ; URL chứa UUID không đoán được.

### Chuỗi cung ứng và bí mật

- Lockfile bắt buộc; Dependabot cho NuGet, pip (uv), npm, GitHub Actions, Docker.
- CI chạy `dotnet list package --vulnerable`, `pnpm audit --prod`, `pip-audit`, quét image bằng Trivy;
  lỗ hổng mức High trở lên chặn merge.
- Image production ghim theo digest; GitHub Actions ghim theo commit SHA.
- Bí mật production ở file `.env` trên VPS (quyền `600`, chủ là user deploy) và GitHub Actions Secrets;
  mỗi role CSDL một mật khẩu riêng.

## Phản ứng sự cố

1. Phát hiện → ghi lại thời điểm, phạm vi, dữ liệu liên quan.
2. Khoanh vùng: khóa tài khoản liên quan, thu hồi token (`uver`), xoay khóa bị nghi lộ.
3. Sự cố lộ dữ liệu có thể gây hại: báo cơ quan chuyên trách trong 72 giờ (đề cương, mục Bảo mật, pháp lý)
   và thông báo người bị ảnh hưởng.
4. Khắc phục, viết postmortem không đổ lỗi, thêm test chống lặp lại.

## Kiểm thử bảo mật

OWASP ZAP baseline chạy hằng đêm trên stack Docker đầy đủ trong CI (workflow `nightly.yml`; dự án không có
môi trường staging riêng); test ma trận phân quyền; test tệp tải lên
(đuôi giả, polyglot, ảnh 100 megapixel, PDF đổi đuôi .jpg); test token (sửa chữ ký, hết hạn, dùng lại
refresh token); test thiếu header CSRF; thí nghiệm E4 về câu chèn. Chi tiết: [09-testing.md](09-testing.md).
