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

Không cần LLM để chạy: khi không có phiên bản `llm` đang chạy (mặc định), chữ được chấm bằng luật dự phòng
(`pipeline/rules.py`). Dev/E2E muốn mức "Cao" ổn định thì đặt `LLM_FAKE=1` và đăng ký dòng `llm fake/deterministic`.
