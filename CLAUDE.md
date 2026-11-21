@AGENTS.md

## Ghi chú riêng cho Claude Code

- Task nhiều file: dùng plan mode, trình bày kế hoạch và chờ duyệt rồi mới sửa file.
- Mỗi task một phiên: `/clear` trước khi sang task mới trong `docs/10-implementation-plan.md`.
- Chạy test bằng lệnh trong `Makefile`; đọc hết thông báo lỗi trước khi sửa.
- Hỏi trước khi chạy lệnh phá dữ liệu hoặc khó đảo ngược: `docker volume rm`, `docker compose down -v`,
  `DROP`, `TRUNCATE`, `git push --force`, `git reset --hard`, xóa thư mục.
- Không sửa `db/migrations/0001_init.sql` và `db/migrations/0002_seed_reference.sql`.
