# Kiểm thử tải (k6) — thí nghiệm E6

Ngưỡng ở [docs/09-testing.md](../../docs/09-testing.md#kiểm-thử-tải-k6-thư-mục-testsload). Chạy từ thư mục gốc repo
(kết quả JSON ghi vào `research/results/e6-<ngày>-<kịch bản>.json`):

```bash
k6 run tests/load/browse.js      -e BASE_URL=https://$DOMAIN
k6 run tests/load/search.js      -e BASE_URL=https://$DOMAIN
k6 run tests/load/quick-check.js -e BASE_URL=https://$DOMAIN      # tạm nâng Quota__GuestScansPerDay trước
# dev: -e BASE_URL=http://localhost:3000 -e API_URL=http://localhost:8080
```

Không cài k6 thì dùng image `grafana/k6:1.4.2`. Với stack tunnel trên máy cá nhân (08-deployment.md, mục Chạy không cần
VPS), chạy k6 **trong mạng Docker của stack** và trỏ vào Caddy. Như vậy request đi qua Caddy → web/api như người dùng thật,
và `SPREAD_IPS=1` có tác dụng, vì Caddy của chế độ tunnel tin `X-Forwarded-For` từ dải IP riêng:

```bash
docker run --rm --network troviec-tunnel_internal -v "$PWD:/work" -w /work grafana/k6:1.4.2   run -e BASE_URL=http://caddy -e SPREAD_IPS=1 tests/load/browse.js
```

- `SPREAD_IPS=1`: mỗi lượt lặp mang một IP riêng (dải 198.18.0.0/15). Không đặt thì mọi request chung một IP và dính
  hạn mức theo IP (`429`) từ rất sớm. Qua Cloudflare hay qua Caddy production thì cờ này không có tác dụng: proxy ghi IP
  thật vào sau IP giả, và API lấy IP thật. Khi đó chỉ chạy tải nhẹ, hoặc tạm nâng hạn mức.
- `RUN_LABEL=<nhãn>`: thêm nhãn vào tên tệp kết quả, để lần chạy thứ hai trong ngày không ghi đè lần đầu.
- Trước khi đo phải làm nóng mô hình: một lượt kiểm tra có ảnh và một truy vấn tìm kiếm. Lần đầu worker tải CLIP và
  BGE-M3 (khoảng 3 GB), mất cả phút.

Tìm sức chịu tối đa của Kiểm tra tin: tăng `-e RATE=12`, `18`… tới khi `scan_processing_ms` p95 vượt 10 s.

Sau mỗi lần chạy, đọc `EXPLAIN (ANALYZE, BUFFERS)` của truy vấn chậm nhất (log `pg_stat_statements` hoặc chạy tay
các truy vấn ở docs/03-database.md, mục Truy vấn mẫu). Chỉ mục mới đi qua migration mới kèm lý do trong comment.
