# Chuẩn bị VPS và deploy production

Hướng dẫn làm **một lần** để `push main` tự deploy lên `https://<domain>`. Bối cảnh, chi phí, rủi ro:
`docs/08-deployment.md`. Mặc định: Oracle Cloud Always Free (Ampere A1, ARM64), Ubuntu 24.04, tên miền `.id.vn`.

Thứ tự: 1 → 2 → 3 → 4 → 5 → 6 → 7. Mỗi bước có lệnh kiểm tra ở cuối.

## 1. Máy chủ

- Ubuntu 24.04 LTS, ≥ 2 vCPU, ≥ 8 GB RAM (≥ 12 GB nếu bật PhoBERT), ≥ 50 GB đĩa.
- Oracle: tạo instance `VM.Standard.A1.Flex`, ảnh Ubuntu 24.04, gắn khóa SSH của bạn (người quản trị, có sudo).

Đăng nhập bằng tài khoản quản trị (`ubuntu` trên Oracle) và chạy:

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo apt-get install -y unattended-upgrades fail2ban ufw
sudo dpkg-reconfigure -plow unattended-upgrades      # chọn Yes

# SSH chỉ bằng khóa
sudo sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sudo systemctl restart ssh
```

## 2. Tường lửa: chỉ 22, 80, 443

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw allow 443/udp        # HTTP/3
sudo ufw enable
```

**Oracle có thêm hai lớp chặn**, thiếu một lớp là Let's Encrypt không cấp được chứng chỉ:

1. Console → Networking → VCN → Security List của subnet → Add Ingress Rules: nguồn `0.0.0.0/0`, TCP cổng `80`
   và `443` (thêm UDP `443` nếu muốn HTTP/3).
2. Ảnh Ubuntu của Oracle có sẵn luật `iptables` chặn mọi cổng trừ 22. Mở 80/443 và lưu lại:

```bash
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 80 -j ACCEPT
sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 443 -j ACCEPT
sudo iptables -I INPUT 6 -p udp --dport 443 -j ACCEPT
sudo netfilter-persistent save
```

Kiểm tra (từ máy bạn, sau bước 6): `curl -I http://<domain>` trả về phản hồi của Caddy.

## 3. Docker và user `deploy`

```bash
curl -fsSL https://get.docker.com | sudo sh          # Docker Engine + Compose v2
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy                       # chạy docker, KHÔNG có sudo
sudo install -d -o deploy -g deploy -m 750 /opt/troviec
```

Khóa SSH riêng cho GitHub Actions (tạo trên máy bạn, **không** dùng lại khóa cá nhân):

```bash
ssh-keygen -t ed25519 -C "troviec-deploy" -f troviec_deploy -N ""
# chép nội dung troviec_deploy.pub vào /home/deploy/.ssh/authorized_keys trên VPS (quyền 600, chủ deploy)
ssh-keyscan -t ed25519 <ip-vps> > known_hosts_vps
```

Kiểm tra: `ssh -i troviec_deploy deploy@<ip-vps> docker ps` chạy được.

> Thành viên nhóm `docker` có quyền tương đương root trên máy. Khóa `troviec_deploy` chỉ để trong GitHub Secrets.

## 4. Tên miền

Bản ghi `A` của tên miền (và `AAAA` nếu VPS có IPv6) trỏ về IP công khai của VPS.
Kiểm tra: `nslookup <domain>` ra đúng IP. Chờ bản ghi có hiệu lực trước bước 6, nếu không Caddy sẽ xin chứng chỉ thất bại
(Let's Encrypt giới hạn số lần thử).

## 5. File `.env` trên VPS

```bash
sudo -iu deploy
cd /opt/troviec
nano .env          # dán nội dung theo .env.example, đặt giá trị thật
chmod 600 .env
```

Giá trị bắt buộc khác với dev:

| Biến | Production |
|---|---|
| `DOMAIN`, `ACME_EMAIL` | tên miền thật, email nhận cảnh báo chứng chỉ |
| `POSTGRES_PASSWORD`, `APP_*_PASSWORD`, `GARAGE_RPC_SECRET`, `Jwt__SigningKey`, `Crypto__*`, `Embed__InternalToken`, `INTERNAL_API_TOKEN` | sinh ngẫu nhiên, **mỗi biến một giá trị** (`openssl rand -base64 32` / `openssl rand -hex 32`) |
| `ConnectionStrings__Main` | `Host=postgres;Port=5432;Database=troviec;Username=app_api;Password=<APP_API_PASSWORD>` |
| `ConnectionStrings__Migrator` | như trên với `app_migrator` |
| `ConnectionStrings__Maintenance` | như trên với `app_maintenance` |
| `ConnectionStrings__Redis` | `redis:6379` |
| `DATABASE_URL` | `postgresql://app_worker:<APP_WORKER_PASSWORD>@postgres:5432/troviec` |
| `REDIS_URL` | `redis://redis:6379/0` |
| `S3__Endpoint`, `S3_ENDPOINT` | `http://garage:3900` |
| `S3__PublicMediaBaseUrl` | `https://<domain>/media/listing-images` |
| `Embed__BaseUrl` | `http://worker-embed:8001` |
| `Cors__AllowedOrigins`, `Jwt__Issuer`, `Jwt__Audience`, `App__SiteUrl` | `https://<domain>` |
| `NEXT_PUBLIC_API_BASE_URL` | để trống (cùng origin qua Caddy) |
| `Sms__Provider` | `none` |
| `Smtp__*` | Brevo SMTP (`smtp-relay.brevo.com:587`) |
| `WORKER_FAKE_PIPELINE`, `LLM_FAKE` | `0` |

`POSTGRES_HOST_PORT` và `ForwardedHeaders__KnownNetworks` không cần: production không mở cổng Postgres, còn mạng proxy
tin cậy đã ghi trong `docker-compose.prod.yml` (`172.30.0.0/24`).

Mật khẩu `APP_*_PASSWORD` chỉ được dùng **lần đầu** PostgreSQL khởi tạo volume. Đổi sau đó phải `ALTER ROLE` trong CSDL.

## 6. GitHub

Repo → Settings:

1. **Secrets and variables → Actions → Secrets**:
   `VPS_HOST` (IP), `VPS_USER` (`deploy`), `VPS_SSH_KEY` (nội dung file `troviec_deploy`),
   `VPS_KNOWN_HOSTS` (nội dung `known_hosts_vps`).
2. **Variables**: `DOMAIN` = tên miền; `DEPLOY_ENABLED` = `true` (bật bước deploy; khi chưa đặt, push `main` chỉ build
   và đẩy image lên GHCR).
3. **Environments**: tạo `production` (job deploy dùng environment này; có thể thêm người duyệt).
4. **Package GHCR**: sau lần `deploy` đầu tiên, GitHub tạo 6 package `troviec-api`, `troviec-migrator`,
   `troviec-maintenance`, `troviec-worker`, `troviec-web`, `troviec-postgres` ở chế độ **private**.
   Vào từng package → Package settings → Change visibility → **Public**, để VPS kéo image không cần token
   (image không chứa bí mật). Muốn giữ private thì trên VPS chạy `docker login ghcr.io` bằng PAT quyền `read:packages`.

Bật deploy lần đầu: Actions → `deploy` → Run workflow (hoặc push một commit vào `main`).

## 7. Lần chạy đầu trên VPS

Sau khi workflow `deploy` chạy xong lần đầu (đã có `docker-compose.prod.yml`, `caddy/`, `garage/`, `DEPLOYED_TAG`):

```bash
sudo -iu deploy && cd /opt/troviec
export TAG=$(cat DEPLOYED_TAG)
C="docker compose -f docker-compose.prod.yml"

# Garage: layout, 3 bucket, 2 khóa. Chép 4 dòng khóa in ra vào .env rồi khởi động lại api, worker.
GARAGE_COMPOSE_FILE=docker-compose.prod.yml GARAGE_ENV_FILE=.env bash garage/init.sh
nano .env
$C up -d api worker worker-embed

# Tài khoản quản trị đầu tiên: đặt Bootstrap__AdminEmail/Password trong .env, chạy, rồi XÓA hai dòng đó.
$C run --rm migrator bootstrap-admin

# Danh mục đơn vị hành chính (khi đã có db/seed/administrative_units/units.csv)
$C run --rm -v "$PWD/units.csv:/app/units.csv:ro" migrator import-units /app/units.csv
```

Kiểm tra:

```bash
curl -fsS https://<domain>/health/live           # Healthy
curl -s -o /dev/null -w "%{http_code}\n" https://<domain>/health/ready   # 404 (chặn từ Internet)
$C exec caddy wget -qO- http://api:8080/health/ready                    # Healthy (từ trong mạng Docker)
$C ps
```

Mở `https://<domain>` trên trình duyệt: trang chủ tạm, ổ khóa hợp lệ (Let's Encrypt).

## 8. Tác vụ định kỳ (systemd timer)

Các file unit nằm ở `infra/vps/systemd/` trong repo. Workflow deploy không chép chúng (cài unit cần sudo), nên chép
bằng tay từ máy bạn rồi cài bằng tài khoản quản trị:

```bash
# trên máy bạn, ở gốc repo
scp infra/vps/systemd/* ubuntu@<ip-vps>:/tmp/

# trên VPS, tài khoản quản trị
sudo install -m 644 /tmp/troviec-*.service /tmp/troviec-*.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now troviec-maintenance.timer
systemctl list-timers 'troviec-*'
```

**Không** bật `troviec-basebackup`, `troviec-dump`, `troviec-media-sync` trước T4.4: lúc đó
image mới có WAL-G và PostgreSQL mới bật `archive_mode`.

## Quay lui

Mỗi lần deploy ghi tag vào `/opt/troviec/DEPLOYED_TAG`; GHCR giữ 5 bản gần nhất.

```bash
cd /opt/troviec
export TAG=<sha của bản trước>     # xem tab Actions → deploy, hoặc trang package trên GHCR
docker compose -f docker-compose.prod.yml up -d
echo $TAG > DEPLOYED_TAG
```

Migration không tự lùi, nên migration phải tương thích ngược (thêm trước, xóa ở bản sau — `docs/03-database.md`).

## Sự cố thường gặp

| Hiện tượng | Nguyên nhân hay gặp |
|---|---|
| Caddy log `challenge failed` / `timeout` | Chưa mở 80/443 ở security list Oracle hoặc `iptables`; DNS chưa trỏ đúng |
| `deploy` báo `Permission denied (publickey)` | Sai `VPS_SSH_KEY` hoặc `authorized_keys` sai quyền (phải 600, chủ `deploy`) |
| `pull` báo `unauthorized` / `denied` | Package GHCR còn private (bước 6.4) |
| `migrator` báo sai mật khẩu | `.env` đổi `APP_*_PASSWORD` sau khi volume `pgdata` đã khởi tạo |
| `/health/ready` trong mạng Docker trả 503 | Thiếu khóa S3 trong `.env` (chưa chạy bước 7) hoặc Redis/Postgres chưa lên |
