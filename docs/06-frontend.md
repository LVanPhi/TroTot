# 06 — Giao diện (PWA)

Giao diện mobile-first, toàn bộ tiếng Việt, cài được lên màn hình chính. Trang quan trọng nhất là
**Kiểm tra tin**: dùng được ngay khi chưa đăng nhập và khi nền tảng chưa có tin nào.

## Nguyên tắc

1. **Mobile-first**: thiết kế cho màn hình 360–430 px trước, rồi mở rộng lên máy tính.
2. **Rủi ro luôn có chữ**: mức rủi ro hiển thị bằng chữ + biểu tượng + màu, không bao giờ chỉ bằng màu.
3. **Câu chữ trung tính**: "Tin này có 3 dấu hiệu rủi ro", không viết "Đây là lừa đảo" (00-overview.md, nguyên tắc 1).
4. **Mọi kết quả AI kèm lời nhắc**: "Kết quả do máy chấm dựa trên dấu hiệu trong nội dung, không phải kết luận
   về người đăng. Luôn xem phòng hoặc gặp nhà tuyển dụng trực tiếp trước khi chuyển tiền."
5. **Không lưu dữ liệu nhạy cảm ở trình duyệt**: access token chỉ trong bộ nhớ; service worker không cache `/api/*`.

## Cấu trúc mã

```text
apps/web/
├── app/
│   ├── layout.tsx · manifest.ts · offline/page.tsx · not-found.tsx
│   ├── (public)/page.tsx                       # /
│   ├── (public)/kiem-tra/page.tsx              # /kiem-tra
│   ├── (public)/kiem-tra/chia-se/route.ts      # dự phòng Web Share Target khi service worker chưa chạy
│   ├── (public)/kiem-tra/[scanId]/page.tsx
│   ├── (public)/tin/page.tsx                   # tìm tin: danh sách + bản đồ
│   ├── (public)/tin/[listingId]/page.tsx       # chi tiết tin (SSR + metadata)
│   ├── (public)/cach-cham-diem/page.tsx        # minh bạch bộ tiêu chí
│   ├── (auth)/dang-nhap · dang-ky · xac-thuc-email · quen-mat-khau · dat-lai-mat-khau
│   ├── (account)/toi/...                       # cần đăng nhập
│   ├── (moderation)/kiem-duyet/...             # cần listing.moderate / report.resolve / ...
│   └── (admin)/quan-tri/...                    # cần quyền admin tương ứng
├── components/
│   ├── ui/                                     # shadcn/ui sinh ra, không sửa tay nhiều
│   └── domain/                                 # RiskBadge, ScoreGauge, EvidenceList, ...
├── lib/
│   ├── api/schema.d.ts                         # sinh bằng openapi-typescript, KHÔNG sửa tay
│   ├── api/client.ts                           # openapi-fetch + middleware gắn token, tự refresh
│   ├── auth/                                   # session trong bộ nhớ, useMe(), can(permission)
│   ├── realtime/scan-hub.ts                    # SignalR
│   ├── format.ts                               # VND, ngày giờ, khoảng cách
│   └── criteria-tips.ts                        # lời khuyên an toàn theo mã tiêu chí
├── public/sw.js · icons/
├── tests/                                      # Vitest
└── e2e/                                        # Playwright
```

## Bản đồ trang

| Đường dẫn | Ai vào được | Nội dung chính | API |
|---|---|---|---|
| `/` | mọi người | Nút lớn "Kiểm tra một tin", ô tìm kiếm câu tự nhiên, tin mới đã xác minh gần trường | `/listings`, `/meta/campuses` |
| `/kiem-tra` | mọi người | Dán nội dung, tải tối đa 5 ảnh, tùy chọn giá + phường/xã; số lượt còn lại (hết lượt khách thì gợi ý đăng nhập) | `POST /scans`, `GET /scans/quota`, `/meta/communes` |
| `/kiem-tra/[scanId]` | ai có link | Trạng thái realtime → kết quả (mục Hiển thị kết quả) | `GET /scans/{id}`, `/hubs/scans` |
| `/tin` | mọi người | Tìm kiếm: ô câu tự nhiên, bộ lọc, chuyển danh sách ↔ bản đồ, chip "điều kiện đã hiểu" từ `appliedFilters` | `/listings`, `/listings/search` |
| `/tin/[listingId]` | mọi người (tin công khai) | Ảnh, giá, bản đồ, tiện nghi, nhãn "Đã xác minh đến dd/MM/yyyy", liên hệ (khi đăng nhập), đánh giá, nút Lưu / Báo cáo | `/listings/{id}` |
| `/cach-cham-diem` | mọi người | Giải thích 4 tín hiệu, danh sách tiêu chí và căn cứ | `/meta/criteria` |
| `/dang-nhap`, `/dang-ky`, `/xac-thuc-email` … | khách | Form xác thực | `/auth/*` |
| `/toi` | đăng nhập | Thông tin tài khoản, xác thực số điện thoại (gặp `503 phone.sms_unavailable` thì hiện hướng dẫn liên hệ quản trị viên thay cho ô nhập mã), việc còn thiếu để đăng tin (`posting.missing`), xóa tài khoản | `/me`, `/auth/phone/*` |
| `/toi/kiem-tra` | đăng nhập | Lịch sử Kiểm tra tin 30 ngày | `/me/scans` |
| `/toi/tin-da-luu` | `listing.save` | Tin đã lưu | `/me/saved-listings` |
| `/toi/tin-cua-toi` | `listing.create` | Tin của tôi theo trạng thái, nút gửi duyệt / gia hạn / gỡ | `/me/listings` |
| `/toi/tin-cua-toi/moi`, `/toi/tin-cua-toi/[id]/sua` | `listing.create` | Form tin phòng / tin việc, tải và sắp ảnh, ghim bản đồ | `/listings`, `/listings/{id}/images` |
| `/toi/tin-cua-toi/[id]` | chủ tin | Dòng thời gian trạng thái, kháng nghị | `/listings/{id}/history`, `/listings/{id}/appeals`, `/me/appeals` |
| `/toi/ho-so-chu` | `listing.create` | Hồ sơ cá nhân/doanh nghiệp, tải giấy tờ (ô đồng ý bắt buộc); sinh viên dùng email trường thì không cần | `/me/owner-profile`, `/me/owner-documents` |
| `/kiem-duyet` | `listing.moderate` | Hàng đợi duyệt (realtime) | `/moderation/queue`, `/hubs/moderation` |
| `/kiem-duyet/tin/[listingId]` | `listing.moderate` | Hai cột: nội dung tin ← → kết quả AI, ảnh trùng, giá, lịch sử, hồ sơ chủ; nút Duyệt / Từ chối / Ẩn | `/moderation/listings/{id}` |
| `/kiem-duyet/bao-cao`, `/kiem-duyet/khang-nghi` | `report.resolve`, `appeal.resolve` | Danh sách + xử lý | `/moderation/reports`, `/moderation/appeals` |
| `/kiem-duyet/ho-so` | `owner.verify` | Hồ sơ chờ duyệt, mở giấy tờ (hộp thoại nhập mục đích) | `/moderation/owner-*` |
| `/kiem-duyet/nhat-ky` | `audit.view_own` | Thao tác của tôi | `/moderation/audit/me` |
| `/quan-tri/nguoi-dung`, `/quan-tri/tieu-chi`, `/quan-tri/mo-hinh`, `/quan-tri/thong-ke`, `/quan-tri/nhat-ky` | quyền admin tương ứng | US-801…805 | `/admin/*` |

Trang bị chặn theo **quyền** (`can("listing.moderate")`), không theo tên vai trò. Vào trang không đủ
quyền → trang 403 thân thiện, không redirect vòng lặp.

Trang quản lý trọ (`/chu-tro/*`, `/toi/thue/*`, `/loi-moi/[token]`, `/toi/tai-khoan-nhan-tien`) thuộc Giai đoạn 1 sau đồ
án: [12-rental.md](12-rental.md#giao-diện).

## Phiên đăng nhập

- Access token giữ trong bộ nhớ (React context / store nhỏ), **không** ghi localStorage.
- Khởi động app: gọi `POST /auth/refresh` một lần (cookie gửi kèm tự động); thành công thì nạp `/me`.
- Middleware của `openapi-fetch`: gắn `Authorization`; gặp `401 auth.unauthenticated` thì refresh **một lần**
  (dùng chung một promise cho các request song song) rồi gửi lại; refresh lỗi → về trạng thái khách.
- Request tới `/auth/refresh` và `/auth/logout` gửi header `X-Requested-With: troviec` (07-security.md).
- Trình duyệt gọi API theo `NEXT_PUBLIC_API_BASE_URL`: production để trống (cùng origin qua Caddy, đường dẫn
  `/api/v1`, `/hubs`); dev là `http://localhost:8080` (khác origin → API bật CORS có credentials cho
  `Cors__AllowedOrigins`, cookie refresh vẫn gửi vì `localhost:3000` và `localhost:8080` cùng site).
- Trang SSR (chi tiết tin, trang chủ) gọi API qua `API_INTERNAL_URL` không kèm token và **chuyển tiếp
  `X-Forwarded-For`** của người dùng (API tin `web` như proxy, 04-api.md mục Hạn mức); phần cần đăng nhập
  (số điện thoại liên hệ, nút Lưu) tải phía client sau khi có phiên.

## Gọi API

- Client sinh từ OpenAPI: `pnpm gen:api` = `openapi-typescript http://localhost:8080/openapi/v1.json -o lib/api/schema.d.ts`.
  CI kiểm `schema.d.ts` khớp với API hiện tại (sinh lại rồi `git diff --exit-code`).
- TanStack Query: query key dạng `["listings", params]`, `["scan", scanId]`, `["me"]`; mutation xong thì
  `invalidateQueries` đúng key. Chỉ dùng cập nhật lạc quan cho Lưu / bỏ lưu.
- Lỗi: đọc `code` trong problem details → thông báo tiếng Việt (bảng ở 04-api.md); lỗi `validation.failed`
  gán vào từng trường của form qua `setError`.
- `409 listing.version_conflict`: hộp thoại "Tin vừa được cập nhật ở nơi khác" với nút "Tải lại".

## Form

- react-hook-form + zod; schema zod phản chiếu validator của API (giới hạn độ dài, khoảng giá…).
- Tiền: ô nhập hiển thị `2.500.000`, gửi số nguyên; chấp nhận gõ "2tr5" và tự đổi.
- Số điện thoại: chấp nhận `0912 345 678`, hiển thị lại dạng chuẩn.
- Chọn phường/xã: combobox tìm theo tên không dấu (`/meta/communes?q=`), hiển thị "Phường X, Thành phố Hà Nội".
- Vị trí: bản đồ với marker kéo được; nút "Dùng vị trí của tôi" (Geolocation, chỉ khi người dùng bấm).
- Ảnh: kéo thả, xem trước, sắp thứ tự (dnd-kit); kiểm tra loại và dung lượng trước khi gửi; tải từng ảnh,
  hiện tiến độ; lỗi một ảnh không làm hỏng các ảnh khác.

## Hiển thị kết quả kiểm tra

Thứ tự trên trang `/kiem-tra/[scanId]`:

1. **Đang xử lý**: skeleton + "Đang phân tích nội dung và hình ảnh…" (thường dưới 10 giây). Kết nối hub,
   gọi `Watch(scanId)`; mất kết nối thì poll `GET /scans/{id}` mỗi 3 giây, dừng khi `completed`/`failed`.
2. **Tóm tắt**: `RiskBadge` + điểm `98/100` + câu tiêu đề:
   - Có dấu hiệu: "Tin này có {n} dấu hiệu rủi ro".
   - Mức Cần thận trọng/Cao nhưng không có dấu hiệu chữ: "Tin này có điểm rủi ro {mức} do {ảnh dùng lại / giá
     thấp bất thường / báo cáo cộng đồng} — xem chi tiết bên dưới".
   - Mức Thấp và không có dấu hiệu: "Chưa thấy dấu hiệu rủi ro rõ ràng".
3. **Lời nhắc** (nguyên tắc 4), luôn hiển thị.
4. **Dấu hiệu** (`EvidenceList`): tên tiêu chí, câu trích trong khung trích dẫn, căn cứ pháp lý nếu có,
   lời khuyên từ `criteria-tips.ts` (ví dụ `deposit_before_viewing` → "Chỉ đặt cọc sau khi đã xem phòng
   và có giấy biên nhận ghi rõ thông tin hai bên"); tiêu chí quản trị viên thêm sau chưa có lời khuyên riêng
   thì dùng lời khuyên chung "Kiểm tra trực tiếp trước khi chuyển tiền hoặc gửi giấy tờ".
5. **Bốn tín hiệu** (`SignalBreakdown`): thanh ngang 0–100% cho mỗi tín hiệu; tín hiệu `null` hiện
   "Không áp dụng" kèm lý do (không có ảnh, chưa đủ 10 phòng để so giá…).
6. **Ảnh dùng lại** (`ImageMatchCard`): "Ảnh số 1 xuất hiện ở 3 tin khác, 2 tin đã bị gỡ".
7. **So sánh giá** (`PriceComparison`, đọc `price.rentVnd` + `price.rentSource`): "Giá nêu 1.000.000 ₫ thấp
   hơn 62% so với trung vị 2.600.000 ₫ của 14 phòng cùng khu vực"; giá trích từ chữ thì viết "Giá đọc được
   trong nội dung …" để người dùng biết hệ thống tự hiểu giá.
8. **Cộng đồng**: "Số điện thoại •••321 đã có 3 báo cáo được cán bộ xác nhận".
9. Nút **Báo cáo** (khi có `report.create`) và **Kiểm tra tin khác**.
10. Khi `failed`: "Chưa phân tích được tin này. Vui lòng thử lại sau ít phút." + mã lỗi nhỏ để hỗ trợ.

| Mức | Nhãn | Biểu tượng (lucide) | Màu nền / chữ (Tailwind) |
|---|---|---|---|
| `low` | Thấp | `ShieldCheck` | `bg-emerald-50` / `text-emerald-800` |
| `caution` | Cần thận trọng | `TriangleAlert` | `bg-amber-50` / `text-amber-900` |
| `high` | Cao | `OctagonAlert` | `bg-red-50` / `text-red-800` |

Độ tương phản chữ/nền tối thiểu 4,5:1 (WCAG AA).

Nhãn loại báo cáo (US-406) — cũng tuân nguyên tắc không gắn chữ "lừa đảo" với một tin:

| `category` | Nhãn hiển thị |
|---|---|
| `scam` | Có dấu hiệu gian lận, đòi tiền |
| `misleading` | Thông tin sai sự thật |
| `inappropriate` | Nội dung không phù hợp |
| `other` | Lý do khác |

## Bản đồ

- maplibre-gl + react-map-gl; style lấy từ `NEXT_PUBLIC_MAP_STYLE_URL` (mặc định nguồn tile vector
  miễn phí không cần key như OpenFreeMap), luôn hiển thị attribution OpenStreetMap.
- Trang tìm tin: marker của các tin trong trang kết quả hiện tại (≤ 50, theo `pageSize` của API), bấm marker
  mở thẻ tin; di chuyển bản đồ hiện nút "Tìm ở khu vực này" (không tự gọi API mỗi lần kéo).
- Tâm mặc định: cơ sở đào tạo người dùng chọn (`/meta/campuses`), nhớ lựa chọn trong `localStorage`
  (chỉ mã cơ sở, không dữ liệu cá nhân).

## PWA

- `app/manifest.ts`: `name` "Trọ & Việc Sạch", `short_name` "Trọ & Việc", `display: "standalone"`,
  `start_url: "/"`, `lang: "vi"`, icon 192/512 + maskable, và:

```ts
share_target: {
  action: "/kiem-tra/chia-se",
  method: "POST",
  enctype: "multipart/form-data",
  params: {
    title: "title",
    text: "text",
    url: "url",
    files: [{ name: "images", accept: ["image/jpeg", "image/png", "image/webp"] }],
  },
},
```

- `public/sw.js` viết tay (không plugin), đăng ký chỉ ở production:
  - Precache `/offline`; điều hướng dùng network-first, mất mạng thì trả `/offline`.
  - **Không cache** `/api/*` (gồm link xem giấy tờ `/api/v1/files/documents/*`) và `/hubs/*`; ảnh tin công
    khai `/media/listing-images/*` để trình duyệt cache theo header của Caddy.
  - Bắt `POST /kiem-tra/chia-se`: đọc `formData`, lưu ảnh + chữ vào Cache Storage `share-target`
    dưới URL tạm, trả `Response.redirect("/kiem-tra?shared=1", 303)`. Trang `/kiem-tra` thấy `shared=1`
    thì đọc lại từ cache, điền form, xóa cache.
  - Dự phòng khi service worker chưa chạy (vừa cài, bị cập nhật): `app/(public)/kiem-tra/chia-se/route.ts` nhận
    `POST` và trả `303` về `/kiem-tra` kèm thông báo "Vui lòng chọn lại ảnh" (không lưu tệp phía máy chủ).
  - Tham số `url` được chia sẻ chỉ ghép vào ô nội dung như chữ thường; **không** fetch (chống SSRF, 07-security.md).
- Web Share Target chỉ chạy trên trình duyệt nhân Chromium của Android; iOS dùng nút "Tải ảnh" bình thường.

## Định dạng

| Dữ liệu | Hiển thị | Cài đặt |
|---|---|---|
| Tiền | `2.500.000 ₫`, rút gọn trên thẻ `2,5 triệu` | `Intl.NumberFormat("vi-VN", {style: "currency", currency: "VND"})` |
| Ngày giờ | `20/11/2026 10:00`, tương đối "3 phút trước" | date-fns + locale `vi`, múi giờ `Asia/Ho_Chi_Minh` |
| Khoảng cách | `350 m`, `1,2 km` | |
| Diện tích | `20 m²` | |
| Lương | `30.000 ₫/giờ` | theo `payUnit`: giờ, buổi, ngày, tháng |

## Thành phần dùng chung

`RiskBadge`, `ScoreGauge`, `EvidenceList`, `SignalBreakdown`, `ImageMatchCard`, `PriceComparison`,
`ListingCard`, `ListingMap`, `LocationPicker`, `CommuneCombobox`, `ImageUploader`, `StatusBadge`
(trạng thái tin: Nháp, Đang sàng lọc, Chờ duyệt, Đã xác minh, Bị từ chối, Tạm ẩn, Đã gỡ, Hết hạn),
`StatusTimeline`, `VersionConflictDialog`, `PermissionGate`, `EmptyState`, `ErrorState`.

## Chất lượng

- Lighthouse (mobile) ≥ 90 cho Performance, Accessibility, Best Practices.
- Vùng chạm ≥ 44 × 44 px; mọi ô nhập có `<label>`; điều hướng được bằng bàn phím; tôn trọng
  `prefers-reduced-motion`.
- Ảnh tin có `width`/`height` để tránh nhảy bố cục; ảnh dưới màn hình đầu dùng `loading="lazy"`.
- Trang chi tiết tin có metadata (tiêu đề, mô tả, ảnh đầu) cho chia sẻ link; tin không công khai trả 404.
- Không dùng `dangerouslySetInnerHTML` cho nội dung người dùng; xuống dòng trong mô tả hiển thị bằng CSS
  `white-space: pre-line`.
