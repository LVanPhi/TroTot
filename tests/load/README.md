# Kiểm thử tải (k6) — thí nghiệm E6

Ngưỡng ở [docs/09-testing.md](../../docs/09-testing.md#kiểm-thử-tải-k6-thư-mục-testsload). Chạy từ thư mục gốc repo
(kết quả JSON ghi vào `research/results/e6-<ngày>-<kịch bản>.json`):

```bash
k6 run tests/load/browse.js      -e BASE_URL=https://$DOMAIN
k6 run tests/load/search.js      -e BASE_URL=https://$DOMAIN
k6 run tests/load/quick-check.js -e BASE_URL=https://$DOMAIN      # tạm nâng Quota__GuestScansPerDay trước
# dev: -e BASE_URL=http://localhost:3000 -e API_URL=http://localhost:8080
```

Tìm sức chịu tối đa của Kiểm tra tin: tăng `-e RATE=12`, `18`… tới khi `scan_processing_ms` p95 vượt 10 s.

Sau mỗi lần chạy, đọc `EXPLAIN (ANALYZE, BUFFERS)` của truy vấn chậm nhất (log `pg_stat_statements` hoặc chạy tay
các truy vấn ở docs/03-database.md, mục Truy vấn mẫu). Chỉ mục mới đi qua migration mới kèm lý do trong comment.
