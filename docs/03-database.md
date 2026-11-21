# 03 — Cơ sở dữ liệu

CSDL là hệ OLTP trên PostgreSQL 18, gồm **32 bảng đạt 3NF** chia 4 nhóm, thống kê chạy trên
materialized view riêng. Nguồn sự thật là các file SQL trong [`db/migrations/`](../db/migrations/);
tài liệu này giải thích quy ước và cung cấp truy vấn mẫu. Ba migration `0001`–`0003` đã được chạy thử
trên PostgreSQL 18.7 + PostGIS 3.7.0 + pgvector 0.8.7 (11/2026, gồm `GRANT MAINTAIN` và
`SET LOCAL hnsw.iterative_scan`) cùng một bộ kiểm thử ràng buộc và phân quyền (xem [09-testing.md](09-testing.md#kiểm-thử-csdl)).

> So với đề cương (31 bảng): thêm `scan_images` để lưu ảnh người dùng gửi khi Kiểm tra tin
> (worker cần object key để đọc ảnh), và bổ sung các cột phục vụ cài đặt (`scans.public_id`,
> `scans.status`, `listings.description`, `listings.search_tsv`, các cột tín hiệu trong `assessments`…).
>
> `0003_review_fixes.sql` (sau đợt rà soát tài liệu): thêm `assessments.price_vnd` (giá đã dùng để so),
> thêm cạnh vòng đời `expired → draft`, ngừng tiện nghi `private_bathroom` (trùng nghĩa `is_self_contained`).
> Không thêm bảng.

## Schema và role

| Schema | Chủ sở hữu | Nội dung |
|---|---|---|
| `app` | `app_migrator` | 32 bảng nghiệp vụ, kiểu enum, hàm, trigger |
| `reporting` | `app_migrator` | Materialized view thống kê, làm mới mỗi đêm |
| `research` | `app_migrator` | Dữ liệu gán nhãn cho thí nghiệm (tạo ở giai đoạn AI, xem 05-ai-worker.md) |
| `public` | superuser | Extension `postgis`, `vector`, `pg_trgm`, `unaccent` |

| Role | Dùng bởi | Quyền chính (chi tiết ở cuối `0001_init.sql`) |
|---|---|---|
| `app_migrator` | CI khi chạy migration, lệnh `bootstrap-admin` | Chủ sở hữu schema; DDL |
| `app_api` | api | Đọc mọi bảng `app`; ghi bảng nghiệp vụ; **không** UPDATE/DELETE bảng chỉ-thêm; không ghi `identifier_stats`, `assessments` |
| `app_worker` | worker | Đọc dữ liệu cần chấm; ghi kết quả chấm; UPDATE theo **cột** (`scans.status`…); với `users` chỉ đọc được cột `id`, `created_at` (tuổi tài khoản); không đọc hồ sơ, giấy tờ, báo cáo |
| `app_maintenance` | job đêm | Xóa lượt kiểm tra hết hạn, chuyển tin hết hạn, dọn outbox, `MAINTAIN` để làm mới view |
| `app_reporting` | notebook, BI | Chỉ đọc schema `reporting` |
| `app_research` | người làm đồ án (notebook gán nhãn, huấn luyện) | Schema `research`; quyền cấp ở migration giai đoạn AI (T4.5). Role do `00_init.sh` tạo vì `app_migrator` không có CREATEROLE |

Search path của mọi role là `app, public`, nên SQL trong code viết `listings` hay `app.listings` đều
được; quy ước là **luôn ghi rõ `app.`** để đọc code không cần nhớ search path.

## Danh mục bảng

| Nhóm | Bảng | Ghi chú |
|---|---|---|
| 1. Người dùng, phân quyền (6) | `users`, `roles`, `permissions`, `user_roles`, `role_permissions`, `owner_profiles` | `user_roles`, `role_permissions` là bảng nối N–N; `owner_profiles` là bảng con 1–1 tùy chọn |
| 2. Tin đăng (10) | `administrative_units`, `listings`, `room_listings`, `job_listings`, `listing_images`, `amenities`, `room_amenities`, `saved_listings`, `reviews`, `listing_transitions` | `room_listings`/`job_listings` là bảng con 1–1 với FK kép `(listing_id, listing_type)`; `listing_transitions` chỉ thêm |
| 3. Sàng lọc AI (12) | `model_versions`, `listing_embeddings`, `scans`, `scan_images`, `criteria`, `assessments`, `assessment_flags`, `assessment_image_matches`, `identifiers`, `scan_identifiers`, `identifier_stats`, `outbox_messages` | `assessments`, `assessment_flags`, `assessment_image_matches` chỉ thêm; `identifier_stats` chỉ trigger ghi |
| 4. Kiểm duyệt (4) | `reports`, `appeals`, `owner_documents`, `document_access_logs` | `document_access_logs` chỉ thêm |

Sơ đồ ERD 13 bảng lõi có trong đề cương; muốn sinh ERD đầy đủ, chạy SchemaSpy hoặc DBeaver trên CSDL dev.

## Quy ước

### Đặt tên

- Tiếng Anh, `snake_case`, chữ thường, không dấu; bảng số nhiều, cột số ít.
- Khóa chính `id`; bảng con 1–1 dùng khóa ngoại làm khóa chính (`listing_id`); bảng nối dùng khóa kép.
- Khóa ngoại `<bảng cha số ít>_id`; khi mang vai trò thì đặt theo vai trò (`poster_id`, `reporter_id`, `decider_id`).
- Hậu tố: `_at` TIMESTAMPTZ, `_vnd` tiền, `_m2` diện tích, `_px` điểm ảnh; boolean bắt đầu `is_`/`has_`.
- Ràng buộc và chỉ mục: `pk_`, `fk_`, `uq_`, `ck_`, `ix_` + bảng + cột/quy tắc. API dựa vào **tên ràng buộc**
  để đổi lỗi CSDL thành mã lỗi (bảng ở cuối file này) — đổi tên ràng buộc phải sửa bảng ánh xạ.
- Tránh từ khóa SQL (`user`, `order`, `group`, `level`); vì vậy cột cấp hành chính tên là `unit_level`.

### Kiểu dữ liệu

| PostgreSQL | C# (Dapper) | Python | JSON (API) |
|---|---|---|---|
| `BIGINT` id nội bộ | `long` | `int` | **không lộ ra API**; API dùng `public_id` |
| `UUID public_id` | `Guid` | `uuid.UUID` | chuỗi UUID |
| `SMALLINT` id danh mục | `short` | `int` | số hoặc dùng `code` |
| `INTEGER` tiền VND | `int` | `int` | số nguyên |
| `NUMERIC(5,1)` diện tích | `decimal` | `Decimal` | số |
| `TIMESTAMPTZ` | `DateTime` (Kind Utc) | `datetime` có tzinfo | ISO 8601 UTC `2026-11-20T10:00:00Z` |
| ENUM (`app.listing_status`…) | `string` trong row, đổi sang enum C# qua `DbEnum` | `str` / `StrEnum` | chuỗi snake_case `"pending_review"` |
| `GEOGRAPHY(Point,4326)` | đọc `lat`/`lng` riêng | đọc `lat`/`lng` riêng | `{"lat": 21.05, "lng": 105.73}` |
| `VECTOR(n)` | `Pgvector.Vector` | `list[float]` / numpy | không lộ ra API |
| `BYTEA` băm | `byte[]` | `bytes` | không lộ ra API |

Quy tắc với Dapper:

- Bật `DefaultTypeMap.MatchNamesWithUnderscores = true` (cột `monthly_rent_vnd` → thuộc tính `MonthlyRentVnd`).
- **Enum**: Dapper gửi enum C# dưới dạng số nên không dùng trực tiếp. Row record khai báo cột enum là
  `string`; tham số truyền chuỗi snake_case và **luôn cast trong SQL**: `@Status::app.listing_status`.
  Đổi qua lại bằng một helper duy nhất `DbEnum.ToDb(ListingStatus.PendingReview)` → `"pending_review"`.
- **Geography**: ghi `ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography`; đọc `ST_Y(location::geometry) AS lat, ST_X(location::geometry) AS lng`.
- **Vector**: dùng gói `Pgvector` + `dataSourceBuilder.UseVector()` và `SqlMapper.AddTypeHandler(new VectorTypeHandler())`.
- Mọi lệnh ghi nhiều bảng chạy trong một `NpgsqlTransaction`; mở connection qua `NpgsqlDataSource` (pool).
- Truy vấn luôn tham số hóa; cấm nối chuỗi SQL với dữ liệu người dùng (kể cả tên cột sắp xếp: dùng whitelist).

### Ý nghĩa NULL

NULL chỉ mang **một** nghĩa cho mỗi cột, ghi ngay trong comment của migration. Chuỗi rỗng bị chặn
bằng `CHECK (btrim(cột) <> '')`. Không dùng giá trị "ma thuật" như `-1` hay `1970-01-01`.

| Cột | NULL nghĩa là |
|---|---|
| `users.email` | Tài khoản đã ẩn danh hóa |
| `users.email_verified_at` | Chưa xác thực email |
| `users.phone_e164` / `phone_verified_at` | Chưa khai báo số / chưa xác thực OTP (chưa được đăng tin) |
| `users.anonymized_at` | Chưa ẩn danh hóa |
| `owner_profiles.business_name`, `tax_code` | Hồ sơ cá nhân (CHECK: doanh nghiệp bắt buộc có cả hai) |
| `owner_profiles.verified_until`, `verifier_id` | Chưa được xác minh |
| `administrative_units.parent_code` | Cấp tỉnh |
| `model_versions.activated_at` | Chưa từng được kích hoạt |
| `listings.expires_at` | Tin chưa từng được duyệt |
| `room_listings.deposit_vnd` | Chủ trọ chưa nêu tiền cọc (`0` = không cần cọc) |
| `listing_images.phash`, `clip_embedding`, `clip_model_id` | Worker chưa tính |
| `reviews.body` | Chỉ chấm sao, không viết nhận xét |
| `reviews.hider_id`, `hidden_reason` | Đánh giá chưa bị ẩn |
| `listing_transitions.from_status` | Bản ghi đầu tiên khi tạo tin |
| `listing_transitions.reason` | Chuyển trạng thái không yêu cầu lý do |
| `scans.listing_id` | Lượt kiểm tra nhanh, không gắn tin |
| `scans.requester_id` | Khách chưa đăng nhập |
| `scans.input_text` | Người dùng chỉ gửi ảnh |
| `scans.claimed_rent_vnd`, `claimed_ward_code` | Người dùng không nêu giá / khu vực |
| `scans.error_code` | Không lỗi (CHECK theo `status`) |
| `scans.completed_at` | Chưa xử lý xong |
| `scans.expires_at` | Lượt sàng lọc tin đăng, sống cùng tin |
| `scan_images.ocr_text` | Ảnh không có chữ đọc được (chỉ có nghĩa khi lượt đã `completed`) |
| `criteria.legal_basis` | Không có căn cứ pháp lý cụ thể |
| `assessments.llm_model_id` | Lượt chấm không gọi LLM |
| `assessments.text_signal` | Không có chữ để chấm |
| `assessments.image_signal` | Không có ảnh |
| `assessments.price_signal` (+ `price_median_vnd`, `price_sample_size`, `price_vnd`) | Không có giá hoặc chưa đủ 10 phòng để so (`price_vnd` thêm ở 0003; dòng cũ có thể NULL dù có tín hiệu) |
| `assessments.community_signal` | Không trích được định danh nào |
| `identifier_stats.last_confirmed_at` | Chưa có báo cáo nào được xác nhận |
| `outbox_messages.scan_id` / `listing_id` | Cung loại trừ: đúng một cột có giá trị (CHECK `num_nonnulls`) |
| `outbox_messages.published_at` | Chưa đẩy sang Redis |
| `reports.listing_id` / `scan_id` | Cung loại trừ như trên |
| `reports.description` | Người báo cáo không ghi thêm |
| `reports.resolver_id`, `resolved_at`, `resolution_note` | Chưa xử lý |
| `appeals.decider_id`, `decided_at`, `decision_note` | Chưa xử lý |
| `owner_documents.object_key` | Tệp đã bị xóa theo yêu cầu (`erased_at` có giá trị) |
| `owner_documents.reviewer_id`, `reviewed_at` | Chưa được duyệt |
| `owner_documents.erased_at` | Tệp còn lưu |

### Khử chuẩn hóa có chủ đích

| Dữ liệu lặp | Vì sao chấp nhận | Cách giữ đồng bộ |
|---|---|---|
| `identifier_stats.confirmed_report_count` | Đọc ở mọi lượt chấm; đếm trực tiếp phải nối 3 bảng | Trigger `on_report_confirmed` (SECURITY DEFINER) khi báo cáo sang `confirmed`. Là bộ đếm lịch sử: xóa báo cáo do hết hạn lưu trữ **không** trừ bộ đếm |
| `listings.expires_at` | Trang công khai và job hết hạn lọc liên tục | Ghi cùng transaction với dòng duyệt trong `listing_transitions` |
| `assessments.risk_level` | Là quyết định đã hiển thị; ngưỡng có thể đổi sau | Không cập nhật lại; `aggregator_model_id` cho biết bộ ngưỡng đã dùng |
| `listing_images.phash`, `clip_embedding`, `listing_embeddings` | Tính tốn kém | Tính lại theo lô khi đổi mô hình, nhận diện qua mã phiên bản |
| Materialized view `reporting.*` | Báo cáo không quét bảng nghiệp vụ lúc đông người | `REFRESH MATERIALIZED VIEW CONCURRENTLY` mỗi đêm |

`appeals.listing_id` **không** phải khử chuẩn hóa: `listing_transition_id` là khóa ứng viên của
`appeals` (UNIQUE), và FK kép `(listing_transition_id, listing_id)` bảo đảm hai cột luôn khớp.
Cột này cần cho UNIQUE từng phần "mỗi tin một kháng nghị đang mở".

### Vòng đời dữ liệu

- **Không xóa cứng** `users`, `listings`. Gỡ tin = `removed`; xóa tài khoản = ẩn danh hóa.
- **Chỉ thêm**: `listing_transitions`, `assessments`, `assessment_flags`, `assessment_image_matches`,
  `document_access_logs` — `app_api` không có quyền UPDATE/DELETE.
- **CASCADE** chỉ dùng cho: bảng con 1–1 của tin, ảnh tin, và mọi dữ liệu phụ của một lượt sàng lọc
  (`scan_images`, `assessments`, `flags`, `matches`, `scan_identifiers`, `outbox_messages`, `reports` trên lượt đó).
  Các khóa ngoại còn lại mặc định: CSDL chặn xóa khi còn tham chiếu. Hệ quả cần biết: xóa một ảnh tin
  (chỉ khi `draft`) xóa theo các dòng `assessment_image_matches` trỏ tới ảnh đó — xem "Hạn chế đã biết" ở 07-security.md.
- **Lưu giữ**: lượt kiểm tra nhanh 30 ngày (`expires_at`); có báo cáo thì API đặt lại `expires_at = now + 1 năm`;
  job đêm xóa lượt hết hạn trừ khi còn báo cáo `open`. Tệp ảnh trên S3 bị xóa **trước** khi xóa dòng.
- **Đổi trạng thái tin** chỉ qua `ListingTransitionService` (BR-09). CHECK `ck_listing_transitions_allowed`
  là lớp chặn cuối, không thay cho kiểm tra ở API. **Ngoại lệ duy nhất**: job đêm (`TroViec.Maintenance`)
  dùng hai câu lệnh hàng loạt ở mục Job đêm bên dưới — vẫn tăng `version` và ghi `listing_transitions`
  trong cùng câu lệnh.

### Ngoại lệ có chủ đích

- Trigger `check_review` (0001) kiểm **tên vai trò** `student` thay vì quyền `review.create`, vì trigger
  không đọc được claim của token. Đây là lớp chặn cuối, tương đương `review.create` theo seed; nếu đổi
  ánh xạ quyền của `review.create` thì phải sửa trigger bằng migration mới.

## Truy vấn mẫu

Các truy vấn dưới đây đã chạy thử trên lược đồ thật (tham số Dapper ghi dạng `@Ten`).

### Đổi trạng thái tin (optimistic concurrency)

```csharp
// ListingTransitionService — nơi DUY NHẤT ghi listings.status
public async Task TransitionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, long listingId,
    int expectedVersion, ListingStatus from, ListingStatus to, long actorId, string? reason,
    DateTime? newExpiresAt = null)
{
    if (!ListingLifecycle.IsAllowed(from, to)) throw new ProblemException("listing.invalid_transition");

    var rows = await conn.ExecuteAsync("""
        UPDATE app.listings
        SET status = @To::app.listing_status,
            version = version + 1,
            expires_at = COALESCE(@NewExpiresAt, expires_at)
        WHERE id = @ListingId
          AND version = @ExpectedVersion
          AND status = @From::app.listing_status
        """, new { ListingId = listingId, ExpectedVersion = expectedVersion,
                   From = DbEnum.ToDb(from), To = DbEnum.ToDb(to), NewExpiresAt = newExpiresAt }, tx);
    if (rows == 0) throw new ProblemException("listing.version_conflict");

    await conn.ExecuteAsync("""
        INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
        VALUES (@ListingId, @From::app.listing_status, @To::app.listing_status, @ActorId, @Reason)
        """, new { ListingId = listingId, From = DbEnum.ToDb(from), To = DbEnum.ToDb(to),
                   ActorId = actorId, Reason = reason }, tx);
}
```

Thao tác tự động dùng `actor_id = app.system_user_id()` (hàm SECURITY DEFINER, không cần quyền đọc `users`).
Trước khi gọi `TransitionAsync`, service kiểm thêm hai luật không nằm trong CSDL: tin có kháng nghị đang mở
thì chỉ cho cạnh do việc xử lý kháng nghị đó (`appeal.pending`), và người thao tác kiểm duyệt khác người đăng
tin (`moderation.conflict_of_interest`). Mọi cạnh sang `verified` (duyệt, chấp nhận kháng nghị từ `rejected`)
truyền `newExpiresAt` và ghi outbox embedding trong cùng transaction.

### Tìm tin công khai có bộ lọc

```sql
SELECT l.public_id, l.listing_type::text AS listing_type, l.title, l.street_address,
       ST_Y(l.location::geometry) AS lat, ST_X(l.location::geometry) AS lng,
       r.monthly_rent_vnd, r.area_m2, r.is_self_contained,
       j.pay_vnd, j.pay_unit::text AS pay_unit,
       ST_Distance(l.location, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography) AS distance_m,
       (SELECT li.object_key FROM app.listing_images li
        WHERE li.listing_id = l.id ORDER BY li.sort_order LIMIT 1) AS cover_key,
       count(*) OVER () AS total
FROM app.listings l
LEFT JOIN app.room_listings r ON r.listing_id = l.id
LEFT JOIN app.job_listings  j ON j.listing_id = l.id
WHERE l.status = 'verified' AND l.expires_at > now()
  AND (@Type::text IS NULL OR l.listing_type = @Type::app.listing_type)
  AND (@MinRent::int IS NULL OR r.monthly_rent_vnd >= @MinRent)
  AND (@MaxRent::int IS NULL OR r.monthly_rent_vnd <= @MaxRent)
  AND ST_DWithin(l.location, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @RadiusM)
  AND (cardinality(@Amenities::text[]) = 0 OR (
        SELECT count(*) FROM app.room_amenities ra
        JOIN app.amenities am ON am.id = ra.amenity_id
        WHERE ra.listing_id = l.id AND am.code = ANY(@Amenities::text[])
      ) = cardinality(@Amenities::text[]))
ORDER BY distance_m          -- whitelist: distance_m | created_at DESC | monthly_rent_vnd
LIMIT @Limit OFFSET @Offset;
```

Thiếu `lat`/`lng` thì API dùng tọa độ cơ sở đầu tiên trong `Campuses__Json`. `@Amenities` chỉ nhận mã
tiện nghi đang dùng (`is_active`).

### Tìm kiếm hybrid (RRF)

`@Query` là phần còn lại của câu sau khi đã tách điều kiện cứng; `@QueryVector` lấy từ dịch vụ embedding,
`@TextEmbeddingModelId` là `modelVersionId` dịch vụ trả về (không trộn vector của hai mô hình).
Nếu không có vector, bỏ CTE `vec`. Nếu `@Query` rỗng, không chạy khối này mà dùng truy vấn tìm tin ở trên.
Chạy cả khối trong một transaction để `SET LOCAL` có hiệu lực.

```sql
SET LOCAL hnsw.iterative_scan = relaxed_order;   -- pgvector 0.8+: lọc xong vẫn đủ kết quả
WITH q AS (
    SELECT app.or_tsquery(@Query) AS tsq, @QueryVector::vector AS qvec),
candidates AS (                                    -- áp mọi bộ lọc cứng ở đây
    SELECT l.id FROM app.listings l
    LEFT JOIN app.room_listings r ON r.listing_id = l.id
    WHERE l.status = 'verified' AND l.expires_at > now()
      AND (@MaxRent::int IS NULL OR r.monthly_rent_vnd <= @MaxRent)
      AND ST_DWithin(l.location, ST_SetSRID(ST_MakePoint(@Lng, @Lat), 4326)::geography, @RadiusM)),
fts AS (
    SELECT l.id, row_number() OVER (ORDER BY ts_rank_cd(l.search_tsv, q.tsq) DESC) AS rnk
    FROM app.listings l JOIN candidates c ON c.id = l.id CROSS JOIN q
    WHERE l.search_tsv @@ q.tsq
    ORDER BY ts_rank_cd(l.search_tsv, q.tsq) DESC LIMIT 100),
vec AS (
    SELECT e.listing_id AS id, row_number() OVER (ORDER BY e.embedding <=> q.qvec) AS rnk
    FROM app.listing_embeddings e JOIN candidates c ON c.id = e.listing_id CROSS JOIN q
    WHERE e.model_version_id = @TextEmbeddingModelId
    ORDER BY e.embedding <=> q.qvec LIMIT 100)
SELECT id, sum(1.0 / (60 + rnk)) AS rrf_score
FROM (SELECT id, rnk FROM fts UNION ALL SELECT id, rnk FROM vec) u
GROUP BY id
ORDER BY rrf_score DESC
LIMIT @Limit OFFSET @Offset;
```

`app.or_tsquery` nối các từ bằng OR (plainto_tsquery mặc định AND mọi từ, quá chặt với câu tự nhiên);
ví dụ `'phong' | 'khep' | 'kin' | 'co' | 'cho' | 'de' | 'xe'`. Bước sau lấy chi tiết các `id` bằng truy vấn tìm tin.

### Hàng đợi duyệt

```sql
SELECT l.public_id, l.title, l.listing_type::text AS listing_type,
       a.score, a.risk_level::text AS risk_level,
       (SELECT count(*) FROM app.assessment_flags f WHERE f.assessment_id = a.id) AS flag_count,
       q.queued_at
FROM app.listings l
JOIN LATERAL (
    SELECT t.created_at AS queued_at FROM app.listing_transitions t
    WHERE t.listing_id = l.id AND t.to_status = 'pending_review'
    ORDER BY t.id DESC LIMIT 1) q ON true
LEFT JOIN LATERAL (
    SELECT a1.id, a1.score, a1.risk_level
    FROM app.scans s JOIN app.assessments a1 ON a1.scan_id = s.id
    WHERE s.listing_id = l.id
    ORDER BY s.created_at DESC, a1.id DESC LIMIT 1) a ON true
WHERE l.status = 'pending_review'
ORDER BY a.score DESC NULLS FIRST,   -- AI lỗi (không có điểm) lên đầu để xem tay
         q.queued_at
LIMIT @Limit OFFSET @Offset;
```

### Kết quả một lượt kiểm tra

```sql
SELECT s.public_id, s.source::text AS source, s.status::text AS status, s.error_code,
       s.created_at, s.completed_at,
       a.id AS assessment_id, a.score, a.risk_level::text AS risk_level,
       a.text_signal, a.image_signal, a.price_signal, a.community_signal,
       a.price_median_vnd, a.price_sample_size, a.price_vnd
FROM app.scans s
LEFT JOIN LATERAL (SELECT * FROM app.assessments a1 WHERE a1.scan_id = s.id
                   ORDER BY a1.id DESC LIMIT 1) a ON true
WHERE s.public_id = @ScanId
  AND s.source = 'quick_check';   -- lượt sàng lọc tin đăng không lộ qua /scans (BR-20) → 404

SELECT c.code, c.name, c.legal_basis, f.evidence_text
FROM app.assessment_flags f JOIN app.criteria c ON c.id = f.criterion_id
WHERE f.assessment_id = @AssessmentId ORDER BY c.id;

SELECT i.kind::text AS kind, i.hint,
       coalesce(st.confirmed_report_count, 0) AS confirmed_reports, st.last_confirmed_at
FROM app.scan_identifiers si
JOIN app.identifiers i ON i.id = si.identifier_id
LEFT JOIN app.identifier_stats st ON st.identifier_id = i.id
WHERE si.scan_id = @ScanInternalId;

-- Ảnh khớp, gộp theo ảnh đầu vào (imageMatches của GET /scans/{scanId})
SELECT m.source_image_no,
       count(DISTINCT li.listing_id)                                                    AS other_listing_count,
       count(DISTINCT li.listing_id) FILTER (WHERE l.status IN ('hidden', 'removed'))   AS removed_listing_count,
       count(DISTINCT l.ward_code) > 1                                                  AS different_location,
       (array_agg(l.public_id ORDER BY m.hamming_distance)
            FILTER (WHERE l.status = 'verified' AND l.expires_at > now()))[1]          AS public_listing_id,
       (array_agg(l.title ORDER BY m.hamming_distance)
            FILTER (WHERE l.status = 'verified' AND l.expires_at > now()))[1]          AS public_listing_title
FROM app.assessment_image_matches m
JOIN app.listing_images li ON li.id = m.listing_image_id
JOIN app.listings l ON l.id = li.listing_id
WHERE m.assessment_id = @AssessmentId
GROUP BY m.source_image_no
ORDER BY m.source_image_no;
```

`different_location` = các tin khớp nằm ở từ hai phường/xã trở lên (dấu hiệu một bộ ảnh dùng cho nhiều
địa chỉ). Màn hình cán bộ cho lượt `listing` so thêm với `ward_code` của chính tin đang chấm.
Tin khớp không công khai chỉ được đếm, không lộ tiêu đề hay id.

### Outbox relay

```sql
-- trong một transaction, mỗi 500 ms hoặc khi có NOTIFY troviec_outbox (API tự gửi sau commit, không dùng trigger)
SELECT id, scan_id, listing_id FROM app.outbox_messages
WHERE published_at IS NULL
ORDER BY id
LIMIT 100
FOR UPDATE SKIP LOCKED;
-- XADD từng việc vào Redis, rồi:
UPDATE app.outbox_messages SET published_at = now() WHERE id = ANY(@Ids);

-- mỗi phút: đẩy lại lượt chưa worker nào nhận sau 5 phút (Redis mất dữ liệu, stream bị cắt)
UPDATE app.outbox_messages o SET published_at = NULL
FROM app.scans s
WHERE o.scan_id = s.id AND s.status = 'queued'
  AND o.published_at < now() - interval '5 minutes';
```

Nếu XADD thành công mà UPDATE lỗi, hoặc việc bị đẩy lại khi hàng đợi chỉ đang dồn, việc sẽ được gửi lại
lần nữa — chấp nhận được vì bên nhận idempotent.

### Định danh (worker)

```sql
INSERT INTO app.identifiers (kind, value_hmac, hint)
VALUES (@Kind::app.identifier_kind, @ValueHmac, @Hint)
ON CONFLICT (kind, value_hmac) DO NOTHING;
SELECT id FROM app.identifiers WHERE kind = @Kind::app.identifier_kind AND value_hmac = @ValueHmac;
INSERT INTO app.scan_identifiers (scan_id, identifier_id) VALUES (@ScanId, @IdentifierId)
ON CONFLICT DO NOTHING;
```

### Ảnh gần trùng (worker)

```sql
-- pHash: khoảng cách Hamming ≤ 10 (quét tuần tự, đủ nhanh với vài chục nghìn ảnh)
SELECT li.id, li.listing_id, bit_count((li.phash # @Phash)::bit(64)) AS hamming
FROM app.listing_images li
WHERE li.phash IS NOT NULL AND li.listing_id <> coalesce(@ExcludeListingId, -1)
  AND bit_count((li.phash # @Phash)::bit(64)) <= 10
ORDER BY hamming LIMIT 20;

-- CLIP: cosine similarity ≥ 0,92 (HNSW); chỉ so vector cùng mô hình đang chạy
SELECT li.id, li.listing_id, 1 - (li.clip_embedding <=> @Clip::vector) AS similarity
FROM app.listing_images li
WHERE li.clip_embedding IS NOT NULL AND li.clip_model_id = @ClipModelId
  AND li.listing_id <> coalesce(@ExcludeListingId, -1)
ORDER BY li.clip_embedding <=> @Clip::vector
LIMIT 20;
```

pHash lưu dạng `BIGINT` có dấu: chuyển 64 bit không dấu của imagehash sang số có dấu trước khi ghi
(`value - 2**64` nếu `value >= 2**63`).

### Mẫu so giá (worker)

```sql
SELECT r.monthly_rent_vnd
FROM app.listings l JOIN app.room_listings r ON r.listing_id = l.id
WHERE l.status IN ('verified', 'expired')
  AND l.updated_at > now() - interval '180 days'
  AND l.id <> coalesce(@ExcludeListingId, -1)
  AND (@IsSelfContained::boolean IS NULL OR r.is_self_contained = @IsSelfContained)  -- kiểm tra nhanh: thường không biết
  AND ST_DWithin(l.location, @Point::geography, 1000);
-- Kiểm tra nhanh không có tọa độ: thay ST_DWithin bằng l.ward_code = @ClaimedWardCode
```

Worker tính trung vị và MAD bằng numpy; dưới 10 mẫu thì `price_signal = NULL` (không đoán). Có tín hiệu
thì ghi cả `price_vnd` (giá đã đem so) cùng `price_median_vnd`, `price_sample_size`.

### Job đêm (role `app_maintenance`)

Đây là ngoại lệ duy nhất của BR-09 (đổi `listings.status` ngoài `ListingTransitionService`): câu lệnh
hàng loạt vẫn tăng `version` và ghi `listing_transitions` cùng câu lệnh, actor là tài khoản system.

```sql
-- 1. Tin quá hạn
WITH u AS (
    UPDATE app.listings SET status = 'expired', version = version + 1
    WHERE status = 'verified' AND expires_at < now()
    RETURNING id)
INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id)
SELECT id, 'verified', 'expired', app.system_user_id() FROM u;

-- 2. Tin bị ẩn quá 7 ngày không có kháng nghị đang mở
WITH h AS (
    SELECT l.id FROM app.listings l
    JOIN LATERAL (SELECT t.created_at FROM app.listing_transitions t
                  WHERE t.listing_id = l.id ORDER BY t.id DESC LIMIT 1) last ON true
    WHERE l.status = 'hidden' AND last.created_at < now() - interval '7 days'
      AND NOT EXISTS (SELECT 1 FROM app.appeals a WHERE a.listing_id = l.id AND a.status = 'open')),
u AS (UPDATE app.listings l SET status = 'removed', version = version + 1
      FROM h WHERE l.id = h.id RETURNING l.id)
INSERT INTO app.listing_transitions (listing_id, from_status, to_status, actor_id, reason)
SELECT id, 'hidden', 'removed', app.system_user_id(), 'Quá hạn kháng nghị 7 ngày' FROM u;

-- 3. Lượt kiểm tra hết hạn: lấy object key, xóa tệp trên S3, rồi xóa dòng (CASCADE)
SELECT si.object_key FROM app.scan_images si JOIN app.scans s ON s.id = si.scan_id
WHERE s.expires_at < now()
  AND NOT EXISTS (SELECT 1 FROM app.reports r WHERE r.scan_id = s.id AND r.status = 'open');
DELETE FROM app.scans s
WHERE s.expires_at < now()
  AND NOT EXISTS (SELECT 1 FROM app.reports r WHERE r.scan_id = s.id AND r.status = 'open');

-- 4. Outbox đã gửi quá 7 ngày
DELETE FROM app.outbox_messages WHERE published_at < now() - interval '7 days';

-- 5. Thống kê
REFRESH MATERIALIZED VIEW CONCURRENTLY reporting.daily_scans;
REFRESH MATERIALIZED VIEW CONCURRENTLY reporting.daily_moderation;
REFRESH MATERIALIZED VIEW CONCURRENTLY reporting.risk_distribution;
```

Lưu ý quyền: `RETURNING` cần quyền `SELECT` trên cột trả về; `app_maintenance` đã có `SELECT` trên
`listings`, `scans`, `scan_images`, `reports`, `appeals`, `listing_transitions`, `outbox_messages`.

## Lỗi CSDL → mã lỗi API

`Infrastructure/Db/PgErrorMapper` bắt `PostgresException` và đổi như sau (mã lỗi đầy đủ ở 04-api.md):

| SQLSTATE | Điều kiện | Mã lỗi API | HTTP |
|---|---|---|---|
| `23505` | `uq_users_email` | `auth.email_taken` | 409 |
| `23505` | `uq_users_phone_e164` | `auth.phone_taken` | 409 |
| `23505` | `uq_listing_images_listing_sha256` | `image.duplicate` | 409 |
| `23505` | `uq_reports_open_listing_reporter`, `uq_reports_open_scan_reporter` | `report.already_open` | 409 |
| `23505` | `uq_reviews_listing_reviewer` | `review.already_exists` | 409 |
| `23505` | `uq_appeals_one_open` | `appeal.already_open` | 409 |
| `23505` | `uq_appeals_listing_transition_id` (quyết định này đã được kháng nghị, kể cả đã xử lý xong) | `appeal.already_used` | 409 |
| `23505` | `uq_owner_documents_sha256` | `document.duplicate` | 409 |
| `23505` | `uq_model_versions_one_active` | `model.activation_conflict` | 409 |
| `23503` | mọi FK | `validation.reference_not_found` | 422 |
| `23514` | mọi CHECK | `validation.constraint_violated` (kèm tên ràng buộc ở môi trường dev) | 422 |
| `P0001` | trigger nghiệp vụ, `MESSAGE` là mã lỗi (`appeal.window_closed`, `review.not_student`…) | đúng mã đó | theo bảng mã lỗi |
| `40001`, `40P01` | xung đột tuần tự / deadlock | thử lại 1 lần, sau đó `system.retry_later` | 503 |

## Quy tắc migration

1. Mỗi thay đổi lược đồ là **một file mới** `NNNN_mo_ta.sql` (4 chữ số, tăng dần). Không sửa file đã
   chạy ở bất kỳ môi trường nào; DbUp lưu nhật ký trong `app.schema_versions`.
2. Mỗi bảng, view, cột mới phải có **GRANT tường minh** cho đúng role ngay trong file đó, kèm comment.
3. Thêm ràng buộc mới đặt tên theo quy ước và cập nhật bảng "Lỗi CSDL → mã lỗi" nếu API cần mã riêng.
4. Chỉ mục trên bảng lớn ở production: tách file riêng dùng `CREATE INDEX CONCURRENTLY`
   (file đó chạy ngoài transaction — đánh dấu `-- dbup:no-transaction` và cấu hình DbUp tương ứng).
5. Mọi migration phải chạy được trên CSDL rỗng (CI, job `db`) **và** trên bản khôi phục từ sao lưu production
   (chạy tay trên VPS hoặc máy cá nhân trước khi merge migration đó; không đưa dữ liệu thật lên CI).
6. Đổi tên ENUM value hoặc xóa cột: làm theo 2 bước (thêm mới → chuyển code → xóa cũ ở release sau).

## Dữ liệu đơn vị hành chính

Bảng `administrative_units` không nằm trong seed vì dữ liệu lớn (34 tỉnh, 3.321 xã theo
Quyết định 19/2025/QĐ-TTg). Cách nạp: [`db/seed/administrative_units/README.md`](../db/seed/administrative_units/README.md).
