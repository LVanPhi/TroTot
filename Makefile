# Trọ & Việc Sạch — lệnh phát triển. Chạy `make help` để xem danh sách.
# Target chưa có cài đặt in ra task sẽ làm nó (docs/10-implementation-plan.md).

.DEFAULT_GOAL := help
COMPOSE := docker compose -f infra/docker-compose.yml --env-file .env
.PHONY: help dev-up dev-down migrate api worker worker-embed web test gen-api

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

api: ## Chạy API (dotnet watch)
	@echo "Chưa có — làm ở T0.4"

worker: ## Chạy worker chấm điểm (consume)
	@echo "Chưa có — làm ở T0.5"

worker-embed: ## Chạy dịch vụ embedding (serve-embed)
	@echo "Chưa có — làm ở T0.5"

web: ## Chạy web (pnpm dev)
	@echo "Chưa có — làm ở T0.6"

test: ## Chạy dotnet test + pytest + vitest
	@echo "Chưa có — làm ở T0.4–T0.6"

gen-api: ## Sinh apps/web/lib/api/schema.d.ts từ /openapi/v1.json
	@echo "Chưa có — làm ở T0.6"
