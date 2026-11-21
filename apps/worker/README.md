# troviec-worker

AI worker của Trọ & Việc Sạch: chấm điểm rủi ro (`consume`) và dịch vụ embedding (`serve-embed`).
Thiết kế: `docs/05-ai-worker.md`.

```bash
uv sync                      # cài phụ thuộc dev (không có torch)
uv sync --extra ml           # thêm torch CPU khi làm pipeline ML
uv run python -m troviec_worker consume
uv run pytest                # cần Docker (testcontainers: Redis, PostgreSQL)
uv run ruff format --check . && uv run ruff check .
```

Pipeline chấm điểm chưa có (T2.x). Đặt `WORKER_FAKE_PIPELINE=1` để worker đánh dấu lượt `completed`
với điểm giả 0 (chỉ dev/E2E).
