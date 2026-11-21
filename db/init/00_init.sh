#!/bin/bash
# =============================================================================
# Chạy MỘT LẦN bởi superuser khi container PostgreSQL khởi tạo lần đầu
# (đặt trong /docker-entrypoint-initdb.d/). Tạo extension, role đăng nhập, schema.
# Bảng và quyền chi tiết do migration (role app_migrator) tạo.
# Mật khẩu lấy từ biến môi trường, truyền qua biến psql để tránh lỗi trích dẫn.
# =============================================================================
set -euo pipefail

psql -v ON_ERROR_STOP=1 \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v db="$POSTGRES_DB" \
     -v migrator_pw="$APP_MIGRATOR_PASSWORD" \
     -v api_pw="$APP_API_PASSWORD" \
     -v worker_pw="$APP_WORKER_PASSWORD" \
     -v maintenance_pw="$APP_MAINTENANCE_PASSWORD" \
     -v reporting_pw="$APP_REPORTING_PASSWORD" \
     -v research_pw="$APP_RESEARCH_PASSWORD" <<'EOSQL'
-- Extension cần superuser (postgis, vector không phải trusted extension)
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;

-- Role đăng nhập, mỗi thành phần một role (xem docs/07-security.md)
CREATE ROLE app_migrator    LOGIN PASSWORD :'migrator_pw';
CREATE ROLE app_api         LOGIN PASSWORD :'api_pw';
CREATE ROLE app_worker      LOGIN PASSWORD :'worker_pw';
CREATE ROLE app_maintenance LOGIN PASSWORD :'maintenance_pw';
CREATE ROLE app_reporting   LOGIN PASSWORD :'reporting_pw';
-- Role cho dữ liệu gán nhãn (schema research). Tạo ở đây vì app_migrator không có CREATEROLE;
-- quyền trên bảng research.* cấp trong migration giai đoạn AI (T4.5).
CREATE ROLE app_research    LOGIN PASSWORD :'research_pw';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO app_migrator, app_api, app_worker, app_maintenance, app_reporting,
                                   app_research;
GRANT CREATE ON DATABASE :"db" TO app_migrator;
-- REFRESH MATERIALIZED VIEW CONCURRENTLY cần bảng tạm
GRANT TEMPORARY ON DATABASE :"db" TO app_migrator, app_maintenance;

-- Schema thuộc app_migrator: migration tạo bảng, GRANT cho các role còn lại
CREATE SCHEMA app       AUTHORIZATION app_migrator;
CREATE SCHEMA reporting AUTHORIZATION app_migrator;
CREATE SCHEMA research  AUTHORIZATION app_migrator;  -- dữ liệu gán nhãn; chỉ người làm đồ án (app_research) truy cập

ALTER ROLE app_migrator    SET search_path = app, public;
ALTER ROLE app_api         SET search_path = app, public;
ALTER ROLE app_worker      SET search_path = app, public;
ALTER ROLE app_maintenance SET search_path = app, public;
ALTER ROLE app_reporting   SET search_path = reporting, public;
ALTER ROLE app_research    SET search_path = research, public;

-- Chặn truy vấn treo làm cạn connection pool
ALTER ROLE app_api         SET statement_timeout = '5s';
ALTER ROLE app_worker      SET statement_timeout = '30s';
ALTER ROLE app_maintenance SET statement_timeout = '10min';
ALTER ROLE app_reporting   SET statement_timeout = '60s';
ALTER ROLE app_research    SET statement_timeout = '60s';
ALTER ROLE app_api         SET idle_in_transaction_session_timeout = '15s';
ALTER ROLE app_worker      SET idle_in_transaction_session_timeout = '60s';
EOSQL
