# 08 — Triển khai và vận hành

Toàn hệ thống chạy trên **một VPS** bằng Docker Compose, Caddy lo HTTPS tự động. Dựng lại từ đầu bằng
một lệnh; mỗi lần merge vào `main` thì GitHub Actions build image và deploy.

## Môi trường

| Môi trường | Mục đích | Cách chạy |
|---|---|---|
| Dev (máy cá nhân) | Viết code | Hạ tầng trong Docker (`infra/docker-compose.yml`), ba app chạy trên máy: `dotnet watch`, `uv run`, `pnpm dev` |
| CI | Kiểm thử mỗi PR | GitHub Actions + Testcontainers |
| Production | Thử nghiệm thật, bảo vệ đồ án | VPS + `infra/docker-compose.prod.yml` |

VPS đề xuất: Ubuntu 24.04 LTS, 4 vCPU, 8 GB RAM (16 GB nếu bật cả PhoBERT), 80 GB SSD, đặt tại Việt Nam
hoặc Singapore. Cài Docker Engine + Compose v2; UFW chỉ mở 22 (SSH bằng khóa), 80, 443; bật
`unattended-upgrades` và fail2ban. Tên miền riêng trỏ bản ghi A về VPS.

## Docker Compose (dev)

`infra/docker-compose.yml` — chỉ hạ tầng, app chạy trên máy để hot reload:

```yaml
name: troviec
services:
  postgres:
    build:
      context: ..
      dockerfile: infra/postgres/Dockerfile
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: troviec
      APP_MIGRATOR_PASSWORD: ${APP_MIGRATOR_PASSWORD}
      APP_API_PASSWORD: ${APP_API_PASSWORD}
      APP_WORKER_PASSWORD: ${APP_WORKER_PASSWORD}
      APP_MAINTENANCE_PASSWORD: ${APP_MAINTENANCE_PASSWORD}
      APP_REPORTING_PASSWORD: ${APP_REPORTING_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql        # PostgreSQL 18+: đường dẫn volume mới
    ports: ["5432:5432"]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d troviec"]
      interval: 5s
      retries: 20

  redis:
    image: redis:8-alpine
    command: ["redis-server", "--appendonly", "yes", "--maxmemory-policy", "noeviction"]
    volumes: [redisdata:/data]
    ports: ["6379:6379"]

  garage:
    image: dxflrs/garage:v2.4.1           # ghim bản v2.x mới nhất lúc cài
    volumes:
      - ./garage/garage.toml:/etc/garage.toml:ro
      - garage-meta:/var/lib/garage/meta
      - garage-data:/var/lib/garage/data
    ports: ["3900:3900", "3902:3902"]

  mailpit:                                 # SMTP giả, xem email ở http://localhost:8025
    image: axllent/mailpit:latest
    ports: ["1025:1025", "8025:8025"]

volumes: { pgdata: {}, redisdata: {}, garage-meta: {}, garage-data: {} }
```

`maxmemory-policy noeviction` là bắt buộc: Redis giữ hàng đợi và refresh token, không được tự xóa khóa.

`infra/postgres/Dockerfile`:

```dockerfile
FROM postgis/postgis:18-3.6
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-18-pgvector \
 && rm -rf /var/lib/apt/lists/*
# WAL-G (chỉ cần ở production): tải binary bản PostgreSQL cho linux-amd64 từ GitHub Releases,
# kiểm tra checksum, chép vào /usr/local/bin/wal-g
COPY db/init/00_init.sh /docker-entrypoint-initdb.d/00_init.sh
```

### Garage

`infra/garage/garage.toml` (một node):

```toml
metadata_dir = "/var/lib/garage/meta"
data_dir = "/var/lib/garage/data"
db_engine = "sqlite"
replication_factor = 1
rpc_bind_addr = "[::]:3901"
rpc_public_addr = "127.0.0.1:3901"
rpc_secret = "<openssl rand -hex 32>"

[s3_api]
s3_region = "garage"
api_bind_addr = "[::]:3900"
root_domain = ".s3.garage.localhost"

[s3_web]
bind_addr = "[::]:3902"
root_domain = ".web.garage.localhost"
index = "index.html"
```

Khởi tạo một lần (lệnh theo tài liệu Garage v2; kiểm tra `garage --help` nếu cú pháp đổi):

```bash
docker compose exec garage /garage status                       # lấy node id
docker compose exec garage /garage layout assign -z dc1 -c 20G <node-id>
docker compose exec garage /garage layout apply --version 1
for b in listing-images scan-images owner-documents; do
  docker compose exec garage /garage bucket create $b
done
docker compose exec garage /garage key create troviec-app        # in ra access key + secret
for b in listing-images scan-images owner-documents; do
  docker compose exec garage /garage bucket allow --read --write $b --key troviec-app
done
docker compose exec garage /garage bucket website --allow listing-images
```

Chỉ `listing-images` bật website mode (đọc công khai qua Caddy). Garage không hỗ trợ bucket công khai
kiểu AWS, nên hai bucket còn lại chỉ truy cập được bằng khóa của ứng dụng.

## Production

`infra/docker-compose.prod.yml` khác bản dev ở các điểm:

- Thêm service `caddy`, `web`, `api`, `worker`, `worker-embed`, và hai service chạy một lần
  `migrator`, `maintenance` (profile `jobs`). Image lấy từ GHCR theo tag `${TAG}` (commit SHA).
- **Không publish cổng** của postgres, redis, garage, worker-embed ra ngoài; chỉ Caddy mở 80/443.
- Mọi service: `restart: unless-stopped`, giới hạn bộ nhớ (`mem_limit`), log `json-file` với
  `max-size: 10m`, `max-file: 5`.
- PostgreSQL bật lưu WAL cho PITR:
  `command: ["postgres", "-c", "archive_mode=on", "-c", "archive_command=wal-g wal-push %p", "-c", "archive_timeout=300"]`
  (`archive_timeout=300` giữ RPO ≤ 5 phút).
- Volume `models` cho worker (`HF_HOME`, trọng số PhoBERT) để không tải lại mô hình mỗi lần deploy.

`infra/caddy/Caddyfile`:

```caddyfile
{
    email {$ACME_EMAIL}
}

{$DOMAIN} {
    encode zstd gzip

    header {
        Strict-Transport-Security "max-age=31536000; includeSubDomains"
        X-Content-Type-Options "nosniff"
        Referrer-Policy "strict-origin-when-cross-origin"
        Permissions-Policy "camera=(), microphone=(), geolocation=(self)"
        X-Frame-Options "DENY"
        -Server
    }

    # /health/ready chỉ dùng trong mạng Docker; Caddy xếp handle theo độ cụ thể của đường dẫn
    handle /health/ready {
        respond 404
    }

    handle /api/*    { reverse_proxy api:8080 }
    handle /hubs/*   { reverse_proxy api:8080 }
    handle /health/* { reverse_proxy api:8080 }

    handle_path /media/listing-images/* {
        reverse_proxy garage:3902 {
            header_up Host listing-images.web.garage.localhost
        }
        header Cache-Control "public, max-age=604800, immutable"
    }

    handle {
        reverse_proxy web:3000
    }

    log {
        output file /data/access.log {
            roll_size 50MiB
            roll_keep_for 336h
        }
        format json
    }
}
```

API phải cấu hình `ForwardedHeaders` với mạng Docker của Caddy là proxy tin cậy để lấy đúng IP người dùng
cho hạn mức.

## Biến môi trường

Tất cả nằm trong `.env` (dev) hoặc `/opt/troviec/.env` (production, quyền `600`). Mẫu đầy đủ: [`.env.example`](../.env.example).
ASP.NET Core đọc khóa lồng bằng `__` (ví dụ `Jwt__SigningKey`).

| Biến | Dùng bởi | Ý nghĩa |
|---|---|---|
| `DOMAIN`, `ACME_EMAIL` | caddy, api, web | Tên miền và email Let's Encrypt |
| `POSTGRES_PASSWORD`, `APP_*_PASSWORD` | postgres | Mật khẩu superuser và 5 role ứng dụng |
| `ConnectionStrings__Main` | api (app_api), maintenance (app_maintenance) | Chuỗi kết nối Npgsql |
| `ConnectionStrings__Migrator` | migrator | Role `app_migrator` |
| `ConnectionStrings__Redis` | api | `redis:6379` |
| `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience` | api | Khóa HS256 ≥ 32 byte (base64), `https://{DOMAIN}` |
| `Auth__StudentEmailDomains` | api | Danh sách tên miền email trường, cách nhau dấu phẩy |
| `S3__Endpoint`, `S3__Region`, `S3__AccessKey`, `S3__SecretKey` | api, maintenance | Garage (`http://garage:3900`, region `garage`) |
| `S3__BucketListingImages`, `S3__BucketScanImages`, `S3__BucketOwnerDocuments` | api, maintenance | Tên bucket |
| `S3__PublicMediaBaseUrl` | api | `https://{DOMAIN}/media/listing-images` |
| `Crypto__IdentifierHmacKey` | api, worker (`IDENTIFIER_HMAC_KEY`) | 32 byte base64, **giống nhau** ở hai nơi |
| `Crypto__DocumentKeys`, `Crypto__ActiveDocumentKeyId` | api | Khóa AES giấy tờ (JSON) và khóa đang dùng |
| `Crypto__SignedUrlKey` | api | Ký link xem giấy tờ |
| `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Password`, `Smtp__From` | api | Gửi email (dev: Mailpit `localhost:1025`) |
| `Sms__Provider` | api | `console` (dev, in OTP ra log) hoặc tên nhà cung cấp |
| `Embed__BaseUrl`, `Embed__InternalToken`, `Embed__TimeoutMs` | api | `http://worker-embed:8001`, token, `800` |
| `Quota__GuestScansPerDay`, `Quota__UserScansPerDay` | api | `5`, `50` |
| `Campuses__Json` | api | `[{"code":"main","name":"Cơ sở chính","lat":21.05,"lng":105.73}]` |
| `Cors__AllowedOrigins` | api | `https://{DOMAIN}` |
| `DATABASE_URL`, `REDIS_URL` | worker | Role `app_worker` |
| `S3_ENDPOINT`, `S3_REGION`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_BUCKET_SCAN_IMAGES`, `S3_BUCKET_LISTING_IMAGES` | worker | Như trên |
| `LLM_BASE_URL`, `LLM_API_KEY`, `LLM_MODEL` | worker | Nhà cung cấp OpenAI-compatible; để trống = chỉ dùng luật |
| `LLM_TIMEOUT_SECONDS`, `LLM_DAILY_CALL_LIMIT` | worker | `7`, `2000` |
| `INTERNAL_API_TOKEN` | worker-embed | Bằng `Embed__InternalToken` |
| `MODEL_DIR`, `HF_HOME` | worker | `/models`, `/models/hf` |
| `API_INTERNAL_URL` | web (server) | `http://api:8080` |
| `NEXT_PUBLIC_SITE_URL`, `NEXT_PUBLIC_MAP_STYLE_URL` | web | URL site, style bản đồ |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | migrator (lệnh `bootstrap-admin`) | Tạo admin đầu tiên, xóa khỏi `.env` sau khi dùng |
| `WALG_S3_PREFIX`, `AWS_ENDPOINT`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `WALG_LIBSODIUM_KEY` | postgres | Nơi lưu sao lưu ở nhà cung cấp khác, khóa mã hóa |

## CI/CD

`.github/workflows/ci.yml` (mỗi PR và push):

| Job | Bước |
|---|---|
| `db` | Build image postgres → chạy migrator trên CSDL rỗng → chạy test CSDL (09-testing.md) |
| `api` | `dotnet build` (cảnh báo là lỗi) → `dotnet test` (Testcontainers) → `dotnet list package --vulnerable` |
| `worker` | `uv sync` → `ruff check` → `ruff format --check` → `pytest` → `pip-audit` |
| `web` | `pnpm install --frozen-lockfile` → `lint` → `typecheck` → `vitest` → `next build` |
| `contract` | Chạy api với CSDL test → sinh lại `schema.d.ts` → `git diff --exit-code` |
| `images` | Build 5 image, quét Trivy (High trở lên là lỗi) |

`.github/workflows/deploy.yml` (push vào `main`, sau khi CI xanh):

1. Build và đẩy image lên GHCR với tag `${{ github.sha }}`: `api`, `migrator`, `maintenance`, `worker`, `web`.
2. SSH vào VPS (khóa deploy riêng, user không có sudo):
   ```bash
   cd /opt/troviec
   export TAG=<sha>
   docker compose -f docker-compose.prod.yml pull
   docker compose -f docker-compose.prod.yml run --rm migrator
   docker compose -f docker-compose.prod.yml up -d --remove-orphans
   curl -fsS https://$DOMAIN/health/live
   ```
3. Hỏng thì quay lại tag trước bằng `TAG=<sha cũ> docker compose … up -d`. Vì vậy migration phải tương thích
   ngược (thêm trước, xóa ở bản sau — 03-database.md, quy tắc migration).

## Tác vụ định kỳ

systemd timer trên VPS (giờ UTC; 19:00 UTC = 02:00 giờ Việt Nam):

| Timer | Lịch | Lệnh |
|---|---|---|
| `troviec-maintenance` | hằng ngày 19:00 UTC | `docker compose -f docker-compose.prod.yml run --rm maintenance` (US-901) |
| `troviec-basebackup` | hằng ngày 19:30 UTC | `docker compose exec -T postgres wal-g backup-push "$PGDATA"` rồi `wal-g delete retain FULL 7 --confirm` |
| `troviec-dump` | Chủ nhật 20:00 UTC | `pg_dump -Fc` → mã hóa → đẩy ra nơi lưu ngoài, giữ 4 bản |
| `troviec-media-sync` | hằng ngày 20:30 UTC | `rclone sync` bucket `listing-images`, `owner-documents` (đã mã hóa) sang nơi lưu ngoài; **không** sao lưu `scan-images` |

## Sao lưu và khôi phục

- Mục tiêu: **RPO ≤ 5 phút, RTO ≤ 1 giờ**.
- WAL đẩy liên tục + base backup hằng đêm → khôi phục về thời điểm bất kỳ trong 7 ngày (PITR);
  `pg_dump` hằng tuần là lớp dự phòng độc lập.
- Nơi lưu sao lưu ở **nhà cung cấp khác** VPS; dữ liệu mã hóa trước khi rời máy (WAL-G libsodium).

Diễn tập khôi phục (mỗi tháng trong thời gian thử nghiệm, ghi biên bản vào báo cáo):

1. Dựng container PostgreSQL rỗng từ cùng image trên máy khác.
2. `wal-g backup-fetch "$PGDATA" LATEST`.
3. Tạo `recovery.signal`; đặt `restore_command = 'wal-g wal-fetch %f %p'` và
   `recovery_target_time = '<thời điểm cần khôi phục>'`.
4. Khởi động, chờ hết recovery, chạy kiểm tra: số dòng các bảng chính, lượt kiểm tra mới nhất, đăng nhập thử.
5. Ghi thời gian từ bước 1 đến bước 4 (RTO thực tế) và dữ liệu mất (RPO thực tế).

## Giám sát

- Uptime monitor bên ngoài kiểm `https://{DOMAIN}/` và `https://{DOMAIN}/health/live` mỗi phút, cảnh báo
  qua email/Telegram. Uptime tháng là số liệu cho chỉ tiêu ≥ 99%.
- `/health/ready` (CSDL, Redis, S3) chỉ dùng trong mạng nội bộ cho healthcheck của Docker.
- Cảnh báo dung lượng đĩa > 80% (script cron), container khởi động lại quá 3 lần/giờ.
- Xem log: `docker compose logs -f api worker`; có thể chạy Dozzle chỉ trên `localhost` và vào qua SSH tunnel.
- Theo dõi riêng: độ dài stream `troviec:scan-requested` (việc tồn đọng), số entry trong
  `troviec:dead-letter`, số lần gọi LLM trong ngày.

## Dữ liệu demo cho buổi bảo vệ

Script `db/seed/demo/` (tạo ở task cuối, 10-implementation-plan.md): tài khoản demo cho từng vai trò (đã
xác thực sẵn), 20 tin phòng và 10 tin việc quanh các cơ sở đào tạo, 5 tin lừa đảo mẫu đã ẩn danh để demo
Kiểm tra tin, và một bộ ảnh dùng lại. Chạy được nhiều lần (idempotent), không chạy ở production thật sau
khi đã mở cho người dùng.
