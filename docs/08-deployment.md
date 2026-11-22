# 08 — Triển khai và vận hành

Toàn hệ thống chạy trên **một VPS** bằng Docker Compose, Caddy lo HTTPS tự động. Dựng lại từ đầu bằng
một lệnh; mỗi lần merge vào `main` thì GitHub Actions build image và deploy.

## Môi trường

| Môi trường | Mục đích | Cách chạy |
|---|---|---|
| Dev (máy cá nhân) | Viết code | Hạ tầng trong Docker (`infra/docker-compose.yml`), ba app chạy trên máy: `dotnet watch`, `uv run`, `pnpm dev` |
| CI | Kiểm thử mỗi PR | GitHub Actions + Testcontainers |
| Production | Thử nghiệm thật, bảo vệ đồ án | VPS + `infra/docker-compose.prod.yml` |

VPS: Ubuntu 24.04 LTS, tối thiểu 2–4 vCPU, 8 GB RAM (≥ 12 GB nếu bật PhoBERT), 50–80 GB SSD. Mặc định dùng
phương án **miễn phí** Oracle Cloud Always Free (Ampere A1, ARM64) — điều kiện và rủi ro ở mục
[Chi phí và dịch vụ miễn phí](#chi-phí-và-dịch-vụ-miễn-phí); chưa có VPS (Oracle đòi thẻ khi đăng ký) thì chạy cả stack
production trên máy cá nhân sau một tunnel miễn phí — mục [Chạy không cần VPS](#chạy-không-cần-vps). Mọi image
build cho cả `linux/amd64` và `linux/arm64` nên đổi giữa hai phương án chỉ là đổi máy. VPS hoặc nơi sao lưu đặt
ngoài Việt Nam là chuyển dữ liệu xuyên biên giới (07-security.md, mục Riêng tư và LLM).
Cài Docker Engine + Compose v2; UFW chỉ mở 22 (SSH bằng khóa), 80, 443 (với Oracle còn phải mở 80/443 trong
security list của VCN và trong `iptables` của image Ubuntu); bật `unattended-upgrades` và fail2ban. Tên miền
riêng trỏ bản ghi A về VPS.

## Chi phí và dịch vụ miễn phí

Mục tiêu là vận hành **0 đồng** trong suốt đồ án. Mọi thành phần trong Docker Compose là mã nguồn mở tự host;
chỉ các dịch vụ bên ngoài dưới đây cần chọn. Hạn mức miễn phí đã kiểm tra ở thời điểm viết (11/2026) và hay
thay đổi — **kiểm lại trang chính thức khi đăng ký** và ghi ngày kiểm vào báo cáo.

| Nhu cầu | Mặc định miễn phí | Lưu ý | Dự phòng |
|---|---|---|---|
| VPS | Oracle Cloud Always Free — máy Ampere A1 (ARM64) | Tài liệu Oracle ghi 4 OCPU / 24 GB; nhiều người dùng báo hạn mức bị giảm còn 2 OCPU / 12 GB từ 8/2026 (vẫn đủ RAM, CPU yếu hơn — đo p95 sớm ở T2.7). Máy "nhàn rỗi" (CPU p95 < 20% trong 7 ngày) có thể bị thu hồi; tải thử nghiệm của đồ án rất thấp nên dễ bị coi là nhàn rỗi — tìm hiểu điều khoản hiện hành (nhiều người nâng tài khoản lên Pay As You Go, vẫn không tính phí tài nguyên Always Free) và luôn có sao lưu ngoài. Vùng gần nhất ở nước ngoài (Singapore, Nhật) → chuyển dữ liệu xuyên biên giới. Đăng ký cần thẻ để xác minh | Máy cá nhân hoặc máy phòng lab chạy `docker-compose.prod.yml` + Cloudflare Tunnel (mục Chạy không cần VPS); máy phải bật trong thời gian thử nghiệm |
| Tên miền | `.id.vn` (VNNIC): công dân Việt Nam 18–23 tuổi được miễn phí; Thông tư 149/2026/TT-BTC nâng thời gian miễn phí lên 3 năm cho tên miền đăng ký mới từ 01/01/2027 | Đăng ký qua nhà đăng ký tên miền .vn, xác thực eKYC bằng CCCD | Tên miền con miễn phí của DuckDNS (Let's Encrypt vẫn cấp chứng chỉ qua HTTP-01) |
| HTTPS | Let's Encrypt qua Caddy | — | Sau tunnel: HTTPS do Cloudflare/ngrok cấp, Caddy chỉ chạy HTTP |
| LLM | **Không dùng LLM**: luật dự phòng + PhoBERT tự host (đã là đường chạy chính khi chưa đăng ký `llm`) | Chất lượng tín hiệu chữ thấp hơn; so sánh trong E1 | Groq gói Free (OpenAI-compatible, giới hạn theo từng mô hình cỡ ~1.000 request/ngày; bật Zero Data Retention). **Không** dùng gói không trả phí của Gemini API vì dữ liệu được dùng để cải thiện sản phẩm và có người đọc (07-security.md) |
| SMS OTP | Không cần: dev dùng `console`; production cán bộ quản trị xác thực số thủ công (`POST /admin/users/{id}/phone`) | Phải gọi điện / gặp trực tiếp chủ số trước khi xác thực | Không cần dự phòng: xác thực thủ công là phương án chính thức của đồ án |
| Email | Brevo gói Free: SMTP, 300 email/ngày, không cần thẻ | Email có chân trang "Sent with Brevo"; cấu hình SPF/DKIM cho tên miền gửi; dev dùng Mailpit | Gmail SMTP với mật khẩu ứng dụng |
| Sao lưu ngoài | Backblaze B2: 10 GB miễn phí, S3 API (WAL-G, rclone dùng được), không cần thẻ | Nhà cung cấp ở nước ngoài → ghi vào hồ sơ đánh giá tác động | IDrive e2 (10 GB miễn phí) |
| Giám sát uptime | UptimeRobot gói Free: 50 monitor, chu kỳ 5 phút | Gói Free chỉ cho mục đích phi thương mại — đồ án phù hợp | Healthchecks.io gói Free cho job định kỳ |
| Cảnh báo | Telegram bot | — | Email |
| CI/CD, registry | Repo **public**: GitHub Actions không giới hạn phút với runner chuẩn, có runner arm64 `ubuntu-24.04-arm` miễn phí; GHCR hiện miễn phí lưu trữ image | Không commit bí mật (đã là quy tắc); repo private thì runner tính theo phút của gói (GitHub Student Developer Pack cho gói Pro) | Build image ngay trên VPS |
| Bản đồ | OpenFreeMap (không cần key) | Luôn hiện attribution OpenStreetMap | Tự host tile |
| Đưa ra Internet khi không có VPS | Cloudflare Tunnel: quick tunnel `*.trycloudflare.com` không cần tài khoản; tunnel có tên cần tên miền quản lý trên Cloudflare (gói Free) | Không mở cổng router; WebSocket (SignalR) chạy được; URL quick tunnel đổi mỗi lần khởi động lại `cloudflared` | ngrok gói Free (1 tên miền tĩnh, nhưng trang HTML hiện trang cảnh báo trước khi vào) |

Hệ quả kỹ thuật của phương án miễn phí: image đa kiến trúc (ARM64), Dockerfile PostgreSQL dựa trên `postgres:18`,
`LLM_DAILY_CALL_LIMIT` thấp hơn hạn mức gói LLM, monitor 5 phút, và hồ sơ đánh giá tác động liệt kê các bên ở
nước ngoài (Oracle, Backblaze, Brevo, nhà cung cấp LLM nếu dùng).

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
      APP_RESEARCH_PASSWORD: ${APP_RESEARCH_PASSWORD}
    volumes:
      - pgdata:/var/lib/postgresql        # PostgreSQL 18+: đường dẫn volume mới
    ports: ["${POSTGRES_HOST_PORT:-5432}:5432"]   # đổi cổng host nếu máy đã có PostgreSQL chiếm 5432
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
    image: dxflrs/garage:v2.4.1           # ghim bản v2.x mới nhất lúc cài (có amd64 + arm64)
    environment:
      GARAGE_RPC_SECRET: ${GARAGE_RPC_SECRET}   # không ghi bí mật vào garage.toml (file được commit)
    volumes:
      - ./garage/garage.toml:/etc/garage.toml:ro
      - garage-meta:/var/lib/garage/meta
      - garage-data:/var/lib/garage/data
    ports: ["3900:3900", "3902:3902"]

  mailpit:                                 # SMTP giả, xem email ở http://localhost:8025
    image: axllent/mailpit:v1.33.1
    ports: ["1025:1025", "8025:8025"]

volumes: { pgdata: {}, redisdata: {}, garage-meta: {}, garage-data: {} }
```

`maxmemory-policy noeviction` là bắt buộc: Redis giữ hàng đợi và refresh token, không được tự xóa khóa.

`infra/postgres/Dockerfile`:

```dockerfile
# postgres:18 là image đa kiến trúc; postgis/postgis:18-3.6 chỉ có bản amd64 nên không chạy được trên VPS ARM.
# Image postgres chính thức đã cấu hình kho apt của PGDG, nên cài PostGIS + pgvector bằng gói của PGDG.
FROM postgres:18
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-18-postgis-3 postgresql-18-pgvector \
 && rm -rf /var/lib/apt/lists/*
# WAL-G (chỉ cần ở production, thêm ở T4.4): tải binary bản PostgreSQL đúng kiến trúc
# (linux-amd64 hoặc linux-aarch64) từ GitHub Releases theo TARGETARCH, kiểm tra checksum, chép vào /usr/local/bin/wal-g
COPY db/init/00_init.sh /docker-entrypoint-initdb.d/00_init.sh
```

Đã chạy thử (11/2026): PostgreSQL 18.7, PostGIS 3.7.0, pgvector 0.8.7; `0001`–`0003` chạy sạch bằng `app_migrator`.
Muốn giữ đúng một bản PostGIS thì ghim phiên bản gói apt và ghim image theo digest ở production.

### Garage

`infra/garage/garage.toml` (một node):

```toml
metadata_dir = "/var/lib/garage/meta"
data_dir = "/var/lib/garage/data"
db_engine = "sqlite"
replication_factor = 1
rpc_bind_addr = "[::]:3901"
rpc_public_addr = "127.0.0.1:3901"
# rpc_secret KHÔNG ghi ở đây: Garage đọc biến môi trường GARAGE_RPC_SECRET (openssl rand -hex 32)

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
docker compose exec garage /garage key create troviec-app        # api, maintenance: in ra access key + secret
for b in listing-images scan-images owner-documents; do
  docker compose exec garage /garage bucket allow --read --write $b --key troviec-app
done
docker compose exec garage /garage key create troviec-worker     # worker: chỉ đọc ảnh
for b in listing-images scan-images; do
  docker compose exec garage /garage bucket allow --read $b --key troviec-worker
done
docker compose exec garage /garage bucket website --allow listing-images
```

Chỉ `listing-images` bật website mode (đọc công khai qua Caddy). Garage không hỗ trợ bucket công khai
kiểu AWS, nên hai bucket còn lại chỉ truy cập được bằng khóa của ứng dụng. Worker dùng khóa `troviec-worker`
(chỉ đọc, không thấy `owner-documents`) theo đặc quyền tối thiểu.

## Production

`infra/docker-compose.prod.yml` khác bản dev ở các điểm:

- Thêm service `caddy`, `web`, `api`, `worker`, `worker-embed`, và hai service chạy một lần
  `migrator`, `maintenance` (profile `jobs`). Image lấy từ GHCR theo tag `${TAG}` (commit SHA), đa kiến trúc.
- **Không publish cổng** của postgres, redis, garage, worker-embed ra ngoài; chỉ Caddy mở 80/443.
- Mọi service: `restart: unless-stopped`, giới hạn bộ nhớ (`mem_limit`), log `json-file` với
  `max-size: 10m`, `max-file: 5`.
- `maintenance` dùng `ConnectionStrings__Maintenance` (role `app_maintenance`), **không** dùng chung
  `ConnectionStrings__Main` của api (cùng file `.env` thì job sẽ chạy bằng `app_api`, thiếu quyền DELETE `scans`).
- PostgreSQL bật lưu WAL cho PITR **từ T4.4**, khi image đã có WAL-G và `WALG_*` đã cấu hình:
  `command: ["postgres", "-c", "archive_mode=on", "-c", "archive_command=wal-g wal-push %p", "-c", "archive_timeout=300"]`
  (`archive_timeout=300` giữ RPO ≤ 5 phút). Trước đó (T0.8) **không** bật `archive_mode`: `archive_command` lỗi
  liên tục khiến PostgreSQL giữ mọi file WAL trong `pg_wal` cho tới khi đầy đĩa.
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

    # Caddyfile không cho viết khối "{ … }" trên một dòng, mỗi handle phải xuống dòng
    handle /api/* {
        reverse_proxy api:8080
    }
    handle /hubs/* {
        reverse_proxy api:8080
    }
    handle /health/* {
        reverse_proxy api:8080
    }

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
        format filter {
            wrap json
            fields {
                request>uri query {
                    delete access_token    # token SignalR trong query không được nằm trong log
                }
            }
        }
    }
}
```

Đã kiểm bằng `caddy validate` (Caddy 2.11.9, 11/2026): cấu hình hợp lệ, bộ lọc xóa `access_token` có trong log.
Sửa Caddyfile thì chạy lại `docker run --rm -e DOMAIN=… -v ./infra/caddy:/etc/caddy caddy:2 caddy validate --config /etc/caddy/Caddyfile`.

API phải cấu hình `ForwardedHeaders` với mạng Docker nội bộ (Caddy và `web`) là proxy tin cậy để lấy đúng IP
người dùng cho hạn mức; `web` chuyển tiếp `X-Forwarded-For` khi SSR (04-api.md, mục Hạn mức).

## Biến môi trường

Tất cả nằm trong `.env` (dev) hoặc `/opt/troviec/.env` (production, quyền `600`). Mẫu đầy đủ: [`.env.example`](../.env.example).
ASP.NET Core đọc khóa lồng bằng `__` (ví dụ `Jwt__SigningKey`).

| Biến | Dùng bởi | Ý nghĩa |
|---|---|---|
| `TAG` | compose production | Commit SHA của image cần chạy (deploy đặt) |
| `DOMAIN`, `ACME_EMAIL` | caddy, api, web | Tên miền và email Let's Encrypt |
| `POSTGRES_PASSWORD`, `APP_*_PASSWORD` | postgres | Mật khẩu superuser và 6 role ứng dụng (`migrator`, `api`, `worker`, `maintenance`, `reporting`, `research`) |
| `POSTGRES_HOST_PORT` | compose dev | Cổng PostgreSQL trên máy host (mặc định 5432); đổi khi máy đã cài PostgreSQL, sửa `Port` trong `ConnectionStrings__*` theo |
| `ASPNETCORE_ENVIRONMENT` | api, migrator, maintenance | `Development` / `Production` (OpenAPI, Scalar chỉ bật ở Development) |
| `ConnectionStrings__Main` | api | Role `app_api` |
| `ConnectionStrings__Maintenance` | maintenance | Role `app_maintenance` (biến riêng vì api và maintenance đọc chung `.env`) |
| `ConnectionStrings__Migrator` | migrator | Role `app_migrator` |
| `ConnectionStrings__Redis` | api | `redis:6379` |
| `Jwt__SigningKey`, `Jwt__Issuer`, `Jwt__Audience` | api | Khóa HS256 ≥ 32 byte (base64), `https://{DOMAIN}` |
| `Jwt__AccessTokenMinutes`, `Jwt__RefreshTokenDays` | api | `15`, `30` |
| `Auth__StudentEmailDomains` | api | Danh sách tên miền email trường, cách nhau dấu phẩy |
| `App__SiteUrl` | api | Gốc URL của web cho link trong email (đặt lại mật khẩu): `https://{DOMAIN}` |
| `GARAGE_RPC_SECRET` | garage | Bí mật RPC (`openssl rand -hex 32`), thay cho `rpc_secret` trong `garage.toml` |
| `S3__Endpoint`, `S3__Region`, `S3__AccessKey`, `S3__SecretKey` | api, maintenance | Garage (`http://garage:3900`, region `garage`), khóa `troviec-app` |
| `S3__BucketListingImages`, `S3__BucketScanImages`, `S3__BucketOwnerDocuments` | api, maintenance | Tên bucket |
| `S3__PublicMediaBaseUrl` | api | `https://{DOMAIN}/media/listing-images` |
| `Crypto__IdentifierHmacKey` | api, worker (`IDENTIFIER_HMAC_KEY`) | 32 byte base64, **giống nhau** ở hai nơi |
| `Crypto__DocumentKeys`, `Crypto__ActiveDocumentKeyId` | api | Khóa AES giấy tờ (JSON) và khóa đang dùng |
| `Crypto__SignedUrlKey` | api | Ký link xem giấy tờ |
| `Smtp__Host`, `Smtp__Port`, `Smtp__User`, `Smtp__Password`, `Smtp__From` | api | Gửi email (dev: Mailpit `localhost:1025`) |
| `Sms__Provider` | api | `console` (chỉ dev, in OTP ra log; API từ chối khởi động với giá trị này ở Production) hoặc tên nhà cung cấp; production chưa có nhà cung cấp thì để `none` và xác thực số thủ công (US-104 AC6) |
| `Embed__BaseUrl`, `Embed__InternalToken`, `Embed__TimeoutMs` | api | `http://worker-embed:8001`, token, `800` |
| `Quota__GuestScansPerDay`, `Quota__UserScansPerDay` | api | `5`, `50` |
| `Campuses__Json` | api | `[{"code":"main","name":"Cơ sở chính","lat":21.05,"lng":105.73}]` |
| `Cors__AllowedOrigins` | api | `https://{DOMAIN}` |
| `ForwardedHeaders__KnownNetworks` | api | Mạng được tin là proxy (CIDR, cách nhau dấu phẩy). Dev `127.0.0.1/32,::1/128`; production đặt subnet cố định của mạng Docker nội bộ (Caddy, `web`) |
| `DATABASE_URL`, `REDIS_URL` | worker | Role `app_worker` |
| `S3_ENDPOINT`, `S3_REGION`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_BUCKET_SCAN_IMAGES`, `S3_BUCKET_LISTING_IMAGES` | worker | Garage, khóa **chỉ đọc** `troviec-worker` |
| `LLM_BASE_URL`, `LLM_API_KEY`, `LLM_MODEL` | worker | Nhà cung cấp OpenAI-compatible; để trống = chỉ dùng luật (0 đồng). `LLM_MODEL` phải trùng `<model-id>` của dòng `llm` đang chạy |
| `LLM_TIMEOUT_SECONDS`, `LLM_DAILY_CALL_LIMIT` | worker | `7`, `800` (thấp hơn hạn mức ngày của gói miễn phí đang dùng) |
| `LLM_FAKE` | worker | `1` = dùng `llm/fake.py` (chỉ dev/CI/E2E); production luôn `0` |
| `LOG_LEVEL` | worker | `INFO` |
| `INTERNAL_API_TOKEN` | worker-embed | Bằng `Embed__InternalToken` |
| `MODEL_DIR`, `HF_HOME` | worker | `/models`, `/models/hf` |
| `API_INTERNAL_URL` | web (server) | `http://api:8080` |
| `NEXT_PUBLIC_API_BASE_URL` | web (trình duyệt) | Trống ở production (cùng origin qua Caddy); `http://localhost:8080` ở dev |
| `NEXT_PUBLIC_SITE_URL`, `NEXT_PUBLIC_MAP_STYLE_URL` | web | URL site, style bản đồ |
| `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` | migrator (lệnh `bootstrap-admin`) | Tạo admin đầu tiên, xóa khỏi `.env` sau khi dùng |
| `WALG_S3_PREFIX`, `AWS_ENDPOINT`, `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `WALG_LIBSODIUM_KEY` | postgres | Nơi lưu sao lưu ở nhà cung cấp khác (mặc định Backblaze B2 qua S3 API), khóa mã hóa |

## CI/CD

`.github/workflows/ci.yml` (mỗi PR và push):

| Job | Bước |
|---|---|
| `db` | Build image postgres → chạy migrator trên CSDL rỗng → chạy test CSDL (09-testing.md) |
| `api` | `dotnet build` (cảnh báo là lỗi) → `dotnet test` (Testcontainers) → `dotnet list package --vulnerable` |
| `worker` | `uv sync` → `ruff check` → `ruff format --check` → `pytest` → `pip-audit` |
| `web` | `pnpm install --frozen-lockfile` → `lint` → `typecheck` → `vitest` → `next build` |
| `contract` | Chạy api ở chế độ Development (không cần CSDL: `/openapi/v1.json` không truy vấn gì) → sinh lại `schema.d.ts` → `git diff --exit-code` |
| `images` | Matrix 5 image × 2 kiến trúc, mỗi kiến trúc build trên runner thật (`ubuntu-24.04` cho amd64, `ubuntu-24.04-arm` cho arm64, không dùng QEMU); quét Trivy, lỗ hổng High/Critical **đã có bản vá** là lỗi (`ignore-unfixed`). CI chỉ build và quét; `deploy.yml` mới đẩy lên GHCR. `api`, `migrator`, `maintenance` dùng chung `apps/api/Dockerfile` (chọn bằng `--target`); image migrator đọc migration từ `MIGRATIONS_DIR=/app/db/migrations` |

`.github/workflows/nightly.yml` (hằng đêm): dựng toàn bộ stack bằng Docker Compose với `LLM_FAKE=1` và dữ liệu
`db/seed/demo` → chạy 4 kịch bản Playwright (T4.2) → OWASP ZAP baseline trên chính stack đó (T4.1). Dự án không
có môi trường staging riêng; đây là nơi duy nhất chạy E2E và ZAP tự động.

`.github/workflows/deploy.yml` (sau khi workflow `ci` xanh trên lần push vào `main`, hoặc chạy tay). Bước deploy chỉ
chạy khi biến repo `DEPLOY_ENABLED=true`; cần Secrets `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY`, `VPS_KNOWN_HOSTS`, biến
`DOMAIN` và environment `production`. Chuẩn bị VPS từng bước: [`infra/vps/README.md`](../infra/vps/README.md).

1. Build và đẩy **6** image đa kiến trúc lên GHCR (`ghcr.io/<owner>/troviec-<tên>`) với tag là commit SHA:
   `api`, `migrator`, `maintenance`, `worker` (cũng dùng cho `worker-embed`), `web`, `postgres` (image
   `infra/postgres` có PostGIS + pgvector). Mỗi kiến trúc build trên runner thật (`<sha>-amd64`, `<sha>-arm64`)
   rồi ghép thành tag đa kiến trúc `<sha>`. Xóa bớt bản cũ, giữ 5 bản gần nhất (đủ để quay lui).
   Package GHCR mới tạo là private: đặt thành public một lần (`infra/vps/README.md`, bước 6).
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
| `troviec-basebackup` | hằng ngày 19:30 UTC | `docker compose -f docker-compose.prod.yml exec -T postgres sh -c 'wal-g backup-push "$PGDATA" && wal-g delete retain FULL 7 --confirm'` (dùng `sh -c` để `$PGDATA` lấy trong container, không phải của máy host) |
| `troviec-dump` | Chủ nhật 20:00 UTC | `pg_dump -Fc` → mã hóa → đẩy ra nơi lưu ngoài, giữ 4 bản |
| `troviec-media-sync` | hằng ngày 20:30 UTC | `rclone sync` bucket `listing-images`, `owner-documents` (đã mã hóa) sang nơi lưu ngoài; **không** sao lưu `scan-images` |

Timer `troviec-basebackup`, `troviec-dump`, `troviec-media-sync` để tắt ở T0.8 và bật ở T4.4 cùng `archive_mode`.

## Chạy không cần VPS

Khi chưa có VPS, chạy **đúng stack production** trên máy cá nhân (Docker Desktop hoặc Linux) và đưa ra Internet
qua tunnel miễn phí. Caddy chạy chế độ HTTP (`infra/caddy/Caddyfile.tunnel`, cùng route, header, lọc
`access_token` như bản production); tunnel lo HTTPS. Không mở cổng router, không cần IP tĩnh.

```bash
cd infra
cp ../.env.example .env            # điền mật khẩu/khóa như production; TAG = tag image trên GHCR
# 1. Quick tunnel (không cần tài khoản), URL ngẫu nhiên:
docker compose -f docker-compose.prod.yml -f docker-compose.tunnel.yml --env-file .env --profile quick up -d
docker compose -f docker-compose.prod.yml -f docker-compose.tunnel.yml logs cloudflared | grep trycloudflare.com
# 2. Đặt DOMAIN=<tên vừa in> (không kèm https://) trong .env rồi chạy lại lệnh up -d để api/web nhận địa chỉ.
# Tunnel có tên (địa chỉ cố định): tạo tunnel ở Cloudflare Zero Trust trỏ về http://caddy:80,
# đặt CLOUDFLARE_TUNNEL_TOKEN và DOMAIN trong .env, dùng --profile token thay cho --profile quick.
```

Sau đó chạy migrator và `seed-demo` như bình thường (`docker compose … run --rm migrator`). Kiểm tại máy:
`http://localhost:8088`.

Giới hạn (ghi vào báo cáo nếu dùng cho thử nghiệm): máy phải bật suốt thời gian thử nghiệm; số đo k6 (E6) là của
máy này chứ không phải VPS ARM 2 OCPU; systemd timer (job đêm, sao lưu) thay bằng Task Scheduler/cron của máy;
dữ liệu nằm trên máy cá nhân nên càng cần sao lưu ngoài (Backblaze B2 vẫn miễn phí).

## Sao lưu và khôi phục

- Mục tiêu: **RPO ≤ 5 phút, RTO ≤ 1 giờ**.
- WAL đẩy liên tục + base backup hằng đêm → khôi phục về thời điểm bất kỳ trong 7 ngày (PITR);
  `pg_dump` hằng tuần là lớp dự phòng độc lập.
- Nơi lưu sao lưu ở **nhà cung cấp khác** VPS (mặc định Backblaze B2, 10 GB miễn phí — đủ cho CSDL, WAL 7 ngày
  và ảnh tin ở quy mô thử nghiệm; theo dõi dung lượng hằng tuần); dữ liệu mã hóa trước khi rời máy (WAL-G libsodium).

Diễn tập khôi phục (mỗi tháng trong thời gian thử nghiệm, ghi biên bản vào báo cáo):

1. Dựng container PostgreSQL rỗng từ cùng image trên máy khác.
2. `wal-g backup-fetch "$PGDATA" LATEST`.
3. Tạo `recovery.signal`; đặt `restore_command = 'wal-g wal-fetch %f %p'` và
   `recovery_target_time = '<thời điểm cần khôi phục>'`.
4. Khởi động, chờ hết recovery, chạy kiểm tra: số dòng các bảng chính, lượt kiểm tra mới nhất, đăng nhập thử.
5. Ghi thời gian từ bước 1 đến bước 4 (RTO thực tế) và dữ liệu mất (RPO thực tế).

## Giám sát

- Uptime monitor bên ngoài (UptimeRobot gói Free) kiểm `https://{DOMAIN}/` và `https://{DOMAIN}/health/live`
  mỗi 5 phút (chu kỳ nhỏ nhất của gói miễn phí), cảnh báo qua email/Telegram bot. Uptime tháng là số liệu cho
  chỉ tiêu ≥ 99% (ghi rõ độ phân giải 5 phút trong báo cáo).
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
