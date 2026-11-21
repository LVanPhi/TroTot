# Trọ & Việc Sạch — lệnh phát triển. Chạy `make help` để xem danh sách.
# Target chưa có cài đặt in ra task sẽ làm nó (docs/10-implementation-plan.md).

.DEFAULT_GOAL := help
COMPOSE := docker compose -f infra/docker-compose.yml --env-file .env
.PHONY: help dev-up dev-down migrate api worker worker-embed web test lint gen-api

help: ## Liệt kê các target
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-14s %s\n", $$1, $$2}'

dev-up: ## Bật hạ tầng dev: postgres, redis, garage, mailpit
	@test -f .env || { echo "Thiếu .env — chép từ .env.example rồi đặt mật khẩu"; exit 1; }
	$(COMPOSE) up -d --build --wait
	bash infra/garage/init.sh

dev-down: ## Tắt hạ tầng dev (giữ dữ liệu)
	$(COMPOSE) down

migrate: ## Chạy db/migrations bằng TroViec.Migrator
	dotnet run --project apps/api/src/TroViec.Migrator -- migrate

api: ## Chạy API (dotnet watch) ở http://localhost:8080
	dotnet watch --project apps/api/src/TroViec.Api

worker: ## Chạy worker chấm điểm (consume)
	cd apps/worker && uv run python -m troviec_worker consume

worker-embed: ## Chạy dịch vụ embedding (serve-embed) ở cổng 8001
	cd apps/worker && uv run python -m troviec_worker serve-embed

web: ## Chạy web (pnpm dev) ở http://localhost:3000
	cd apps/web && pnpm dev

test: ## Chạy dotnet test + pytest + vitest
	dotnet test --solution apps/api/TroViec.slnx
	cd apps/worker && uv run pytest
	cd apps/web && pnpm test

lint: ## Kiểm tra định dạng và lint (ruff)
	cd apps/worker && uv run ruff format --check . && uv run ruff check .

gen-api: ## Sinh apps/web/lib/api/schema.d.ts từ /openapi/v1.json (API phải đang chạy; đổi OPENAPI_URL nếu khác cổng)
	cd apps/web && pnpm gen:api
