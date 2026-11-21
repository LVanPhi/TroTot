# Worker image (consume and serve-embed). Multi-arch: linux/amd64 and linux/arm64.
FROM python:3.12-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-vie \
 && rm -rf /var/lib/apt/lists/*

COPY --from=ghcr.io/astral-sh/uv:0.13.8 /uv /usr/local/bin/uv

WORKDIR /app
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never \
    PATH="/app/.venv/bin:$PATH" PYTHONUNBUFFERED=1

# Dependencies first (cached layer), CPU-only torch from the pytorch-cpu index.
COPY pyproject.toml uv.lock README.md ./
RUN uv sync --frozen --no-dev --extra ml --no-install-project

COPY src ./src
RUN uv sync --frozen --no-dev --extra ml

RUN useradd --system --uid 10001 worker
USER worker

ENTRYPOINT ["python", "-m", "troviec_worker"]
CMD ["consume"]
