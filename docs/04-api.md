# 04 — Đặc tả API

REST JSON dưới `https://{DOMAIN}/api/v1`, cộng hai hub SignalR. Tài liệu OpenAPI 3.1 được sinh tự
động từ code tại `/openapi/v1.json` (chỉ bật ở dev/CI); frontend sinh client TypeScript từ file này.
**Thứ tự ưu tiên khi lệch:** tài liệu này là hợp đồng → sửa code cho khớp, hoặc sửa tài liệu trong
cùng PR và sinh lại client.

## Quy ước chung

| Chủ đề | Quy ước |
|---|---|
| Định dạng | JSON `camelCase`; enum là chuỗi snake_case giống CSDL (`"pending_review"`, `"high"`) |
| ID | Tin và lượt kiểm tra dùng UUID công khai (`listingId` = `listings.public_id`, `scanId` = `scans.public_id`). Tài nguyên nội bộ (ảnh tin, báo cáo, kháng nghị, giấy tờ, người dùng trong trang admin) dùng số `id` và **luôn kiểm tra quyền sở hữu** |
| Quyền | Cột "Quyền" ghi: `khách` = `AllowAnonymous()`; `đăng nhập` = `RequireAuthorization()` không kèm quyền (chỉ dữ liệu của chính người gọi); mã quyền = `RequireAuthorization("<mã>")`; `chủ tin` = có `listing.create` **và** `poster_id = sub` |
| Thời gian | ISO 8601 UTC (`2026-11-20T03:00:00Z`); client tự đổi sang giờ Việt Nam |
| Tiền | Số nguyên VND (`monthlyRentVnd: 2500000`) |
| Tọa độ | `{"lat": 21.0537, "lng": 105.7353}`; chấp nhận lat 8–24, lng 102–110 |
| Phân trang | `?page=1&pageSize=20` (tối đa 50) → `{"items": [], "page": 1, "pageSize": 20, "total": 134}` |
| Xác thực | `Authorization: Bearer <accessToken>`; refresh token trong cookie `tv_refresh` |
| Đồng thời | Thao tác sửa tin gửi kèm `version`; lệch → `409 listing.version_conflict` |
| Tệp | `multipart/form-data`; giới hạn ở BR-06 |
| Lỗi | RFC 9457 `application/problem+json` (mục Lỗi) |
| Hạn mức | Header `RateLimit-Limit`, `RateLimit-Remaining`, `RateLimit-Reset`; khi `429` có `Retry-After` |
| Ngôn ngữ | `title`/`detail` trong lỗi viết tiếng Việt; client vẫn nên hiển thị theo `code` |

### Lỗi

```json
{
  "type": "https://troviec.example.com/errors/listing.version_conflict",
  "title": "Tin vừa được cập nhật ở nơi khác",
  "status": 409,
  "code": "listing.version_conflict",
  "detail": "Tải lại trang để xem phiên bản mới nhất rồi thao tác lại.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": null
}
```

Lỗi kiểm tra dữ liệu dùng `code = "validation.failed"`, `status = 400`, và `errors` là map
`tên trường (camelCase) → danh sách thông báo tiếng Việt`.

| Mã | HTTP | Khi nào | Thông báo gợi ý |
|---|---|---|---|
| `validation.failed` | 400 | Dữ liệu không qua validator | Dữ liệu chưa hợp lệ |
| `validation.reference_not_found` | 422 | Vi phạm khóa ngoại | Dữ liệu tham chiếu không tồn tại |
| `validation.constraint_violated` | 422 | Vi phạm CHECK lọt qua validator | Dữ liệu không thỏa ràng buộc |
| `auth.unauthenticated` | 401 | Thiếu / sai access token | Vui lòng đăng nhập |
| `auth.forbidden` | 403 | Thiếu quyền | Bạn không có quyền thực hiện thao tác này |
| `auth.invalid_credentials` | 401 | Sai email hoặc mật khẩu | Email hoặc mật khẩu không đúng |
| `auth.locked_temporarily` | 423 | Sai mật khẩu 5 lần / 15 phút | Thử lại sau 15 phút |
| `auth.account_locked` | 403 | Tài khoản bị khóa | Tài khoản đã bị khóa |
| `auth.email_not_verified` | 403 | Đăng nhập khi chưa xác thực email | Vui lòng xác thực email |
| `auth.email_taken` | 409 | Email đã dùng | Email đã được đăng ký |
| `auth.student_email_domain` | 422 | Sinh viên dùng email ngoài trường | Dùng email do trường cấp |
| `auth.weak_password` | 422 | Mật khẩu yếu | Mật khẩu cần ít nhất 10 ký tự và không quá phổ biến |
| `auth.code_invalid` | 422 | Mã email/OTP sai hoặc hết hạn | Mã không đúng hoặc đã hết hạn |
| `auth.code_attempts_exceeded` | 429 | Nhập sai quá số lần | Xin mã mới |
| `auth.phone_taken` | 409 | Số đã gắn tài khoản khác | Số điện thoại đã được dùng |
| `auth.refresh_invalid` | 401 | Refresh token sai / hết hạn | Phiên đăng nhập đã hết |
| `auth.refresh_reused` | 401 | Refresh token cũ bị dùng lại | Phiên bị thu hồi vì lý do an toàn |
| `scan.input_required` | 422 | Không có chữ lẫn ảnh | Dán nội dung hoặc tải ít nhất một ảnh |
| `scan.quota_exceeded` | 429 | Vượt hạn mức BR-05 | Bạn đã dùng hết lượt hôm nay |
| `scan.not_found` | 404 | Sai `scanId` hoặc đã xóa | Không tìm thấy lượt kiểm tra |
| `image.unsupported_type` | 415 | Magic bytes không phải JPEG/PNG/WebP | Chỉ nhận ảnh JPEG, PNG, WebP |
| `image.too_large` | 413 | > 5 MB | Ảnh tối đa 5 MB |
| `image.too_many` | 422 | Vượt số ảnh | Quá số ảnh cho phép |
| `image.duplicate` | 409 | Ảnh trùng trong cùng tin | Ảnh này đã có trong tin |
| `document.unsupported_type` | 415 | Giấy tờ không phải JPEG/PNG/WebP/PDF (magic bytes) | Chỉ nhận ảnh hoặc PDF |
| `document.too_large` | 413 | Giấy tờ > 10 MB | Tệp tối đa 10 MB |
| `resource.not_found` | 404 | Báo cáo, kháng nghị, giấy tờ, ảnh, người dùng không tồn tại hoặc không được xem | Không tìm thấy |
| `listing.not_found` | 404 | Không có / không được xem (gồm tin chưa công khai của người khác) | Không tìm thấy tin |
| `listing.not_owner` | 403 | Thao tác của chủ tin trên tin **đang công khai** của người khác (tin chưa công khai → `listing.not_found`, không lộ sự tồn tại) | — |
| `listing.version_conflict` | 409 | `version` lệch | Tin vừa được cập nhật ở nơi khác |
| `listing.invalid_transition` | 409 | Chuyển trạng thái không hợp lệ | Không thể thực hiện ở trạng thái hiện tại |
| `listing.not_editable` | 409 | Sửa khi không ở `draft` | Chuyển tin về nháp trước khi sửa |
| `listing.poster_not_verified` | 403 | Chưa đạt BR-02; `missing` liệt kê việc còn thiếu | Hoàn tất xác minh để gửi duyệt |
| `listing.images_required` | 422 | Tin phòng chưa có ảnh khi gửi duyệt | Thêm ít nhất một ảnh |
| `listing.renew_too_early` | 409 | Gia hạn khi còn > 7 ngày | Chỉ gia hạn khi tin sắp hết hạn |
| `report.already_open` | 409 | Đã có báo cáo đang mở | Bạn đã báo cáo mục này |
| `review.already_exists` | 409 | Đã đánh giá tin này | — |
| `review.not_student` / `review.own_listing` | 403 | Trigger `check_review` | — |
| `appeal.already_open` | 409 | Đã có kháng nghị đang mở | — |
| `appeal.already_used` | 409 | Quyết định này đã được kháng nghị (kể cả đã xử lý xong) | Mỗi quyết định chỉ kháng nghị một lần |
| `appeal.pending` | 409 | Chuyển trạng thái tin khi tin đang có kháng nghị mở (BR-04) | Tin đang chờ xử lý kháng nghị |
| `appeal.window_closed` | 409 | Quá 7 ngày | Đã quá hạn kháng nghị |
| `appeal.target_invalid` / `appeal.decision_superseded` | 409 | Không có quyết định từ chối/ẩn đang hiệu lực | — |
| `appeal.decider_conflict` | 403 | Người xử lý là người ra quyết định | Kháng nghị phải do cán bộ khác xử lý |
| `moderation.conflict_of_interest` | 403 | Cán bộ xử lý tin, hồ sơ, báo cáo, kháng nghị liên quan tới chính mình (BR-19) | Việc này phải do cán bộ khác xử lý |
| `owner.profile_required` | 409 | Tải giấy tờ khi chưa có hồ sơ chủ | Tạo hồ sơ trước |
| `owner.no_accepted_document` | 409 | Xác minh hồ sơ khi chưa có giấy tờ nào `accepted` | Duyệt ít nhất một giấy tờ trước |
| `me.listing_in_screening` | 409 | Xóa tài khoản khi còn tin đang sàng lọc | Có tin đang được sàng lọc, thử lại sau ít phút |
| `document.consent_required` | 422 | Chưa đồng ý xử lý dữ liệu | — |
| `document.duplicate` | 409 | Tệp đã được tải lên (ở tài khoản này hoặc tài khoản khác) | Tệp này đã được tải lên |
| `document.token_invalid` | 403 | Link xem giấy tờ sai / quá 5 phút | Link đã hết hạn |
| `document.erased` | 410 | Tệp đã bị xóa | — |
| `model.activation_conflict` | 409 | Hai phiên bản cùng active | — |
| `admin.cannot_remove_own_admin` | 409 | Tự thu hồi vai trò admin | — |
| `admin.cannot_lock_self` | 409 | Tự khóa tài khoản của mình | — |
| `rate_limited` | 429 | Vượt hạn mức chung | Thao tác quá nhanh |
| `system.retry_later` | 503 | Deadlock, CSDL bận | Thử lại sau giây lát |

## Xác thực — `/auth`

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| POST | `/auth/register` | khách | `{email, password, fullName, accountType: "student" \| "owner"}` → `201`; gửi mã email |
| POST | `/auth/email/verify` | khách | `{email, code}` → `204` |
| POST | `/auth/email/resend` | khách | `{email}` → luôn `204` (không lộ email có tồn tại hay không) |
| POST | `/auth/login` | khách | `{email, password}` → `200 {accessToken, expiresIn, user: Me}` + cookie |
| POST | `/auth/refresh` | cookie | → `200 {accessToken, expiresIn}` + cookie mới |
| POST | `/auth/logout` | cookie | Thu hồi họ refresh token → `204` |
| POST | `/auth/password/forgot` | khách | `{email}` → luôn `204` |
| POST | `/auth/password/reset` | khách | `{token, newPassword}` → `204`; thu hồi mọi refresh token |
| POST | `/auth/phone/otp` | đăng nhập | `{phone}` (chấp nhận `0912…` hoặc `+84912…`) → `204`; số chờ xác thực chỉ lưu trong Redis cùng OTP |
| POST | `/auth/phone/verify` | đăng nhập | `{phone, code}` → `204`; lúc này mới ghi `phone_e164` + `phone_verified_at` (BR-12) |

Access token: JWT HS256, hết hạn 15 phút, claim `sub` (user id), `perm` (danh sách quyền),
`ver` (số đổi khi khóa tài khoản, đổi vai trò, đặt lại mật khẩu, xóa tài khoản — API so với Redis để
thu hồi sớm). Đổi vai trò chỉ tăng `ver`: lần refresh kế tiếp phát token với quyền mới, không cần đăng nhập lại.
Refresh token: chuỗi ngẫu nhiên 256 bit, cookie `tv_refresh; HttpOnly; Secure; SameSite=Strict;
Path=/api/v1/auth; Max-Age=2592000`. Chi tiết xoay vòng: [07-security.md](07-security.md#phiên-đăng-nhập).

## Tài khoản của tôi — `/me`

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/me` | đăng nhập | Thông tin tài khoản, vai trò, quyền, điều kiện đăng tin |
| PATCH | `/me` | đăng nhập | `{fullName}` |
| DELETE | `/me` | đăng nhập | `{password}` → `204`; ẩn danh hóa và gỡ mọi tin (US-106); còn tin `screening` → `409 me.listing_in_screening` |
| GET | `/me/scans` | đăng nhập | Lượt Kiểm tra tin của tôi trong 30 ngày (`source = quick_check`, phân trang) |
| GET | `/me/listings?status=` | `listing.create` | Tin của tôi mọi trạng thái |
| GET | `/me/saved-listings` | `listing.save` | Tin đã lưu (kèm tin hết hạn, gắn nhãn) |
| PUT / DELETE | `/me/saved-listings/{listingId}` | `listing.save` | Lưu / bỏ lưu → `204` (idempotent) |
| GET | `/me/appeals` | `listing.appeal` | Kháng nghị của tôi |
| GET / PUT | `/me/owner-profile` | `listing.create` | `{ownerType, businessName?, taxCode?}` |
| GET | `/me/owner-documents` | `listing.create` | Loại giấy tờ, trạng thái duyệt, ngày tải (không trả tệp) |
| POST | `/me/owner-documents` | `listing.create` | multipart `file`, `docType`, `consent=true` → `201 {id}` |
| DELETE | `/me/owner-documents/{id}` | `listing.create` + chủ hồ sơ | Xóa tệp, giữ dòng với `erasedAt` → `204` |

```json
// GET /me → 200
{
  "email": "a.nguyen@sv.example.edu.vn",
  "fullName": "Nguyễn Văn A",
  "phone": "+84912345678",
  "emailVerified": true,
  "phoneVerified": false,
  "roles": ["student"],
  "permissions": ["scan.create", "listing.create", "listing.save", "listing.appeal", "report.create", "review.create"],
  "posting": { "allowed": false, "missing": ["phone_verification"] }
}
```

`posting.missing` gồm các giá trị: `email_verification`, `phone_verification`, `owner_verification`.

## Kiểm tra tin — `/scans`

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| POST | `/scans` | khách hoặc `scan.create` | Tạo lượt kiểm tra → `202` |
| GET | `/scans/quota` | khách hoặc đăng nhập | `200 {limit, remaining, resetAt}` theo IP (khách) hoặc tài khoản; không trừ lượt |
| GET | `/scans/{scanId}` | ai biết `scanId` (UUID không đoán được) | Trạng thái + kết quả |
| POST | `/scans/{scanId}/reports` | `report.create` | `{category, description?}` → `201 {id}`; giữ lượt kiểm tra 1 năm |

Mọi endpoint `/scans*` (và `Watch` của ScanHub) chỉ phục vụ lượt `source = quick_check`; `scanId` của lượt
sàng lọc tin đăng trả `404 scan.not_found` (BR-20 — người đăng không dò được điểm AI để lách).

`POST /scans` (multipart):

| Trường | Kiểu | Bắt buộc | Ghi chú |
|---|---|---|---|
| `text` | string ≤ 10.000 | một trong hai | Nội dung tin dán vào |
| `images` | file[] ≤ 5 | một trong hai | JPEG/PNG/WebP ≤ 5 MB mỗi ảnh |
| `claimedRentVnd` | int | không | Giá thuê nêu trong tin (bật tín hiệu giá) |
| `claimedWardCode` | string | không | Mã phường/xã (5 chữ số) |

```json
// 202 Accepted
{ "scanId": "4a3bc123-5402-4caa-acfa-532e379b5889", "status": "queued" }
```

```json
// GET /scans/{scanId} → 200 (status: queued | processing | completed | failed)
{
  "scanId": "4a3bc123-5402-4caa-acfa-532e379b5889",
  "status": "completed",
  "createdAt": "2026-11-20T03:00:00Z",
  "completedAt": "2026-11-20T03:00:06Z",
  "errorCode": null,
  "assessment": {
    "score": 98,
    "riskLevel": "high",
    "signals": { "text": 0.9, "image": 1.0, "price": 0.95, "community": 0.78 },
    "flags": [
      {
        "code": "deposit_before_viewing",
        "name": "Đòi cọc trước khi cho xem phòng",
        "evidence": "chuyển cọc giữ chỗ ngay, liên hệ [SĐT]",
        "legalBasis": null
      }
    ],
    "imageMatches": [
      {
        "sourceImageNo": 1,
        "otherListingCount": 3,
        "removedListingCount": 2,
        "differentLocation": true,
        "publicListing": { "listingId": "3ff5edcd-…", "title": "Phòng trọ …" }
      }
    ],
    "price": { "rentVnd": 1000000, "rentSource": "claimed", "medianVnd": 2600000, "sampleSize": 14 },
    "community": [
      { "kind": "phone", "hint": "•••321", "confirmedReports": 3, "lastConfirmedAt": "2026-11-02T08:00:00Z" }
    ]
  },
  "canReport": true
}
```

Số liệu trong ví dụ khớp bộ gộp `agg-v0` (05-ai-worker.md): 3 báo cáo đã xác nhận → `community = 1 − e^(−3/2) ≈ 0,78`;
ảnh khớp tin đã bị gỡ → `image = 1,0`; một dấu hiệu nên không cộng thưởng → `score = 98`.

- `assessment` là `null` khi chưa xong hoặc lỗi; `signals.*` là `null` khi tín hiệu không áp dụng.
- `evidence` đã che PII (`[SĐT]`, `[STK]`, `[TÊN]`). API **không** có endpoint trả lại ảnh hay nội dung gốc của lượt
  kiểm tra (kể cả cho người gửi, vì khách không định danh được); chỉ cán bộ xử lý báo cáo trên lượt đó xem được
  (`GET /moderation/reports/{id}`).
- `imageMatches` (một phần tử cho mỗi ảnh đầu vào có khớp, truy vấn ở 03-database.md): `otherListingCount` = số tin
  khác dùng ảnh này; `removedListingCount` = số tin khớp đang `hidden`/`removed`; `differentLocation` = các tin khớp
  nằm ở ≥ 2 phường/xã; `publicListing` chỉ có khi một tin khớp đang công khai — tin không công khai chỉ được đếm.
- `price.rentVnd` = `assessments.price_vnd` (giá đã đem so); `rentSource` = `claimed` (người dùng nhập) hoặc
  `extracted` (trích từ chữ). `price` là `null` khi không có tín hiệu giá.

## Tin đăng — công khai

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/listings` | khách | Lọc: `type`, `minRent`, `maxRent`, `lat`, `lng` (thiếu → cơ sở đầu tiên trong `Campuses__Json`), `radiusM` (≤ 10.000, mặc định 3.000), `amenities` (`wifi,parking`), `selfContained`, `sort` (`distance` \| `newest` \| `rent_asc`), phân trang |
| GET | `/listings/search?q=` | khách | Câu tự nhiên + các tham số lọc như trên; trả thêm `appliedFilters` đã tách từ câu |
| GET | `/listings/{listingId}` | khách | Chi tiết; `contactPhone` chỉ có khi đã đăng nhập (BR-14) |
| GET | `/listings/{listingId}/reviews` | khách | Đánh giá đã công khai |
| POST | `/listings/{listingId}/reviews` | `review.create` | `{rating: 1..5, body?}` → `201`; chỉ tin đang công khai hoặc đã hết hạn |
| POST | `/listings/{listingId}/reports` | `report.create` | `{category, description?}` → `201` |

```json
// phần tử trong items của GET /listings
{
  "listingId": "3ff5edcd-fa5a-484d-8ee2-c7174001e7f0",
  "listingType": "room",
  "title": "Phòng trọ khép kín gần ĐH Công nghiệp",
  "wardName": "Phường …",
  "streetAddress": "Số 1 ngõ 2 đường Cầu Diễn",
  "location": { "lat": 21.0537, "lng": 105.7353 },
  "coverImageUrl": "https://troviec.example.com/media/listing-images/3ff5edcd-fa5a-484d-8ee2-c7174001e7f0/9f2c41d0-5b7e-4f7a-a0a8-2f4f6c1d2e3b.webp",
  "room": { "monthlyRentVnd": 2500000, "areaM2": 20.0, "isSelfContained": true },
  "job": null,
  "distanceM": 350,
  "verifiedUntil": "2027-02-18T03:00:00Z"
}
```

```json
// GET /listings/search?q=phòng khép kín dưới 2,5 triệu có chỗ để xe → 200
{
  "items": [ /* như trên */ ],
  "page": 1, "pageSize": 20, "total": 7,
  "appliedFilters": { "type": "room", "maxRent": 2500000, "amenities": ["parking"], "selfContained": true },
  "mode": "hybrid"
}
```

`mode` là `hybrid`, `fulltext` (khi dịch vụ embedding không trả lời kịp) hoặc `filter` (phần câu còn lại rỗng,
chỉ áp bộ lọc). "Khép kín" chỉ ánh xạ sang `selfContained` (tiện nghi `private_bathroom` đã ngừng dùng ở 0003).

Chi tiết tin (`GET /listings/{id}`) gồm thêm: `description`, `images[] {url, width, height}`,
`amenities[] {code, name}`, `room.depositVnd`, `job {employerName, payVnd, payUnit, hoursPerWeek}`,
`poster {displayName, isBusiness, businessName, memberSince, verifiedOwner}`, `contactPhone`,
`reviewSummary {average, count}`, `isSaved` (khi đăng nhập). Người đăng và cán bộ xem được tin ở mọi
trạng thái (kèm `status`, `version`); người khác chỉ thấy tin công khai (BR-01), còn lại `404`.

## Tin đăng — người đăng

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| POST | `/listings` | `listing.create` | Tạo nháp → `201 {listingId, version}` |
| PUT | `/listings/{listingId}` | chủ tin | Sửa nháp, kèm `version` → `200 {version}` |
| POST | `/listings/{listingId}/images` | chủ tin | multipart `file` → `201 {imageId, url, sortOrder}` (chỉ khi `draft`) |
| DELETE | `/listings/{listingId}/images/{imageId}` | chủ tin | → `204` (chỉ khi `draft`) |
| PUT | `/listings/{listingId}/images/order` | chủ tin | `{imageIds: [..]}` → `204` (chỉ khi `draft`) |
| POST | `/listings/{listingId}/submit` | chủ tin | `{version}` → `202 {status: "screening", version}` |
| POST | `/listings/{listingId}/renew` | chủ tin | `{version}` → `202` (BR-03, còn ≤ 7 ngày hoặc đã hết hạn; BR-02 kiểm lại) |
| POST | `/listings/{listingId}/unpublish` | chủ tin | `{version}`: `verified`/`rejected`/`expired` → `draft` |
| POST | `/listings/{listingId}/remove` | chủ tin | `{version, reason?}` → `removed` (không khi `screening`; có kháng nghị mở → `appeal.pending`) |
| GET | `/listings/{listingId}/history` | chủ tin, hoặc `listing.moderate` | Dòng thời gian trạng thái; với chủ tin, tên cán bộ thay bằng "Cán bộ xác minh" |
| POST | `/listings/{listingId}/appeals` | `listing.appeal` + chủ tin | `{reason}`; nhắm vào quyết định từ chối/ẩn mới nhất → `201 {id}` |

Mọi thao tác đổi trạng thái ở trên trả `409 appeal.pending` khi tin đang có kháng nghị mở (BR-04).
Người đăng không xem được điểm và dấu hiệu AI của tin mình (BR-20); `GET /listings/{id}` cho chủ tin chỉ có
`status`, `version` và lý do từ chối/ẩn trong `history`.

```json
// POST /listings — tạo tin phòng
{
  "listingType": "room",
  "title": "Phòng trọ khép kín gần ĐH Công nghiệp",
  "description": "Phòng 20m2 khép kín, có điều hòa, nóng lạnh, giờ giấc tự do, gần trường.",
  "wardCode": "00001",
  "streetAddress": "Số 1 ngõ 2 đường Cầu Diễn",
  "location": { "lat": 21.0537, "lng": 105.7353 },
  "room": {
    "monthlyRentVnd": 2500000,
    "depositVnd": null,
    "areaM2": 20.0,
    "isSelfContained": true,
    "amenityCodes": ["wifi", "air_conditioner"]
  },
  "job": null
}
```

Đúng một trong `room` / `job` khác `null` và khớp `listingType` (`validation.failed` nếu sai).
Validator phản chiếu ràng buộc CSDL: tiêu đề 1–150 ký tự sau khi trim, mô tả 30–5.000, `wardCode`
phải là cấp xã đang tồn tại, giá thuê 100.000–100.000.000, tiền cọc ≥ 0 hoặc `null`, diện tích 0,1–500,
`payVnd` 1–100.000.000, `hoursPerWeek` 1–48, `payUnit ∈ {hour, shift, day, month}`, `amenityCodes` thuộc
danh mục đang dùng (`is_active`).

## Kiểm duyệt — `/moderation`

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/moderation/queue` | `listing.moderate` | Hàng đợi (truy vấn ở 03-database.md) |
| GET | `/moderation/listings/{listingId}` | `listing.moderate` | Tin + kết quả AI + lịch sử + báo cáo + người đăng (họ tên, SĐT, hồ sơ chủ, danh sách giấy tờ) |
| POST | `/moderation/listings/{listingId}/approve` | `listing.moderate` | `{version}` → `verified`, `expiresAt = now + 90 ngày`, tạo outbox embedding |
| POST | `/moderation/listings/{listingId}/reject` | `listing.moderate` | `{version, reason}` |
| POST | `/moderation/listings/{listingId}/hide` | `listing.moderate` | `{version, reason}` |
| POST | `/moderation/listings/{listingId}/restore` | `listing.moderate` | `{version}` (`hidden` → `verified`); có kháng nghị mở → `appeal.pending` (xử lý qua kháng nghị) |
| GET | `/moderation/reports?status=open` | `report.resolve` | Báo cáo theo trạng thái |
| GET | `/moderation/reports/{id}` | `report.resolve` | Báo cáo + đối tượng: tin (như màn hình duyệt) hoặc lượt kiểm tra (`inputText`, kết quả AI, danh sách ảnh) |
| GET | `/moderation/reports/{id}/scan-images/{imageNo}` | `report.resolve` | Ảnh của lượt kiểm tra bị báo cáo (`image/webp`, `Cache-Control: no-store`); chỉ khi báo cáo nhắm vào lượt đó |
| POST | `/moderation/reports/{id}/confirm` | `report.resolve` | `{note, hideListing: bool, listingVersion?}` — nếu `hideListing`, ẩn tin trong cùng transaction (lý do = `note`) |
| POST | `/moderation/reports/{id}/dismiss` | `report.resolve` | `{note}` |
| GET | `/moderation/appeals?status=open` | `appeal.resolve` | |
| POST | `/moderation/appeals/{id}/accept` | `appeal.resolve` | `{note}` → tin về `verified`; từ `rejected` thì `expiresAt = now + 90 ngày` và tạo outbox embedding |
| POST | `/moderation/appeals/{id}/reject` | `appeal.resolve` | `{note}` → tin `hidden` thành `removed` (lý do = `note`); tin `rejected` giữ nguyên |
| GET | `/moderation/owner-profiles?pending=true` | `owner.verify` | Hồ sơ có giấy tờ chờ duyệt |
| GET | `/moderation/owner-profiles/{userId}` | `owner.verify` | Hồ sơ + danh sách giấy tờ (không có tệp) |
| POST | `/moderation/owner-documents/{id}/review` | `owner.verify` | `{decision: "accepted" \| "rejected"}` |
| POST | `/moderation/owner-documents/{id}/access` | `document.view` | `{purpose}` → `200 {url, expiresAt}`; mỗi lần cấp link ghi một dòng `document_access_logs` (BR-10) |
| POST | `/moderation/owner-profiles/{userId}/verify` | `owner.verify` | → `{verifiedUntil}` (now + 12 tháng); chưa có giấy tờ `accepted` → `owner.no_accepted_document` |
| POST | `/moderation/reviews/{id}/hide` | `review.moderate` | `{reason}` |
| GET | `/moderation/audit/me` | `audit.view_own` | Quyết định và lần mở giấy tờ của chính tôi |
| GET | `/files/documents/{token}` | token trong URL | Giải mã và trả tệp; `Cache-Control: no-store`; token hết hạn 5 phút |

Mọi thao tác quyết định trả về `{status, version}` mới và đẩy `ListingUpdated` qua hub kiểm duyệt.
Thao tác nhắm vào tin, hồ sơ, giấy tờ, báo cáo hay kháng nghị liên quan tới chính người gọi (người gọi là
người đăng tin / chủ hồ sơ) → `403 moderation.conflict_of_interest` (BR-19).

## Quản trị — `/admin`

| Phương thức | Đường dẫn | Quyền | Mô tả |
|---|---|---|---|
| GET | `/admin/users?q=&role=` | `user.manage` | Tìm theo email/họ tên (không trả tài khoản `system`) |
| POST | `/admin/users/{id}/roles` | `user.manage` | `{roleCode}` → `204`; tăng `ver` |
| DELETE | `/admin/users/{id}/roles/{roleCode}` | `user.manage` | → `204`; tăng `ver`; không tự thu hồi `admin` của mình |
| POST | `/admin/users/{id}/lock` / `unlock` | `user.manage` | Khóa thì thu hồi mọi refresh token và tăng `ver`; không tự khóa mình (`admin.cannot_lock_self`) |
| POST | `/admin/users/{id}/phone` | `user.manage` | `{phone}` → `204`; xác thực số thủ công khi chưa có nhà cung cấp SMS (US-104 AC6): đặt `phone_e164` + `phone_verified_at`; số đã thuộc tài khoản khác → `auth.phone_taken`; ghi log có cấu trúc `{adminId, userId}` (không log số) |
| GET / POST | `/admin/criteria` | `criteria.manage` | Danh sách / thêm tiêu chí |
| PUT | `/admin/criteria/{id}` | `criteria.manage` | `{name, guidance, scope, legalBasis, isActive}` (không đổi `code`) |
| GET | `/admin/model-versions` | `model.manage` | Theo thành phần, đánh dấu phiên bản đang chạy |
| POST | `/admin/model-versions/{id}/activate` | `model.manage` | Tắt phiên bản đang chạy cùng thành phần rồi bật phiên bản này, một transaction |
| GET | `/admin/stats?from=&to=` | `stats.view` | Từ `reporting.*` |
| GET | `/admin/audit/transitions` | `audit.view_all` | Lọc `actorId`, `listingId`, `from`, `to` |
| GET | `/admin/audit/document-access` | `audit.view_all` | Lọc `viewerId`, `documentId`, `from`, `to` |

## Danh mục — `/meta`

| Phương thức | Đường dẫn | Mô tả |
|---|---|---|
| GET | `/meta/provinces` | 34 tỉnh |
| GET | `/meta/provinces/{code}/communes` | Xã/phường của tỉnh |
| GET | `/meta/communes?q=` | Gợi ý theo tên, không dấu cũng được (trigram trên `immutable_unaccent(lower(name))`) |
| GET | `/meta/amenities` | Tiện nghi đang dùng (`is_active`) |
| GET | `/meta/criteria` | Bộ tiêu chí đang dùng (trang "Hệ thống chấm thế nào", minh bạch với người dùng) |
| GET | `/meta/campuses` | Các cơ sở đào tạo (từ cấu hình `Campuses__Json`) làm tâm tìm kiếm mặc định |

Các endpoint `/meta` được cache 1 giờ (`Cache-Control: public, max-age=3600`).

## Sức khỏe hệ thống

`GET /health/live` (tiến trình còn chạy) và `GET /health/ready` (kết nối được PostgreSQL, Redis, S3).
Không nằm dưới `/api/v1`, không lộ chi tiết lỗi ra ngoài; Caddy chặn `/health/ready` từ Internet.

## SignalR

| Hub | Xác thực | Client gọi | Server gửi |
|---|---|---|---|
| `/hubs/scans` | Không bắt buộc | `Watch(scanId: string)` → tham gia nhóm `scan:{scanId}` (chỉ lượt `quick_check`); tối đa 10 nhóm/kết nối | `ScanUpdated {scanId, status}` |
| `/hubs/moderation` | `listing.moderate` | — | `QueueChanged {pendingCount}`, `ListingUpdated {listingId, status, version}` |

Access token cho WebSocket truyền qua query `access_token` (chuẩn SignalR); API chỉ đọc query này
trên đường dẫn `/hubs/*`, và log request của API lẫn log Caddy phải lọc bỏ tham số này (07-security.md).
Client nhận `ScanUpdated` xong thì gọi `GET /scans/{scanId}` để lấy kết quả đầy đủ (hub không mang dữ liệu nhạy cảm).

## Hạn mức

| Phạm vi | Giới hạn | Cách đếm |
|---|---|---|
| Toàn API theo IP | 300 request/phút | ASP.NET Core RateLimiter, cửa sổ trượt |
| `POST /auth/login` | 10/phút/IP; khóa tài khoản 15 phút sau 5 lần sai | RateLimiter + Redis |
| `POST /auth/register` | 5/giờ/IP | RateLimiter |
| `POST /auth/email/resend`, `POST /auth/password/forgot` | 3/giờ/email, 10/giờ/IP (vẫn trả `204` khi vượt, không lộ email) | Redis + RateLimiter |
| `POST /auth/phone/otp` | 3/giờ/số, 10/ngày/tài khoản | Redis |
| `POST /scans` | Khách 5/ngày/IP; đăng nhập 50/ngày/tài khoản (BR-05); chỉ trừ khi tạo được lượt (`202`) | Redis `quota:scan:{ip|user}:{yyyyMMdd}`, ngày theo giờ Việt Nam |
| `GET /listings/search` | 60/phút/IP | RateLimiter |
| Tải ảnh, giấy tờ | 60/giờ/tài khoản | RateLimiter |

IP lấy từ header `X-Forwarded-For` **chỉ khi** request đến từ proxy tin cậy: Caddy và `web` (SSR chuyển
tiếp IP người dùng) — cấu hình `KnownNetworks` là mạng Docker nội bộ. Nếu không, mọi trang SSR tính chung
một IP của container `web` và dính hạn mức 300 request/phút.
