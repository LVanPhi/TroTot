# postgres:18 is multi-arch; postgis/postgis:18-3.6 is amd64-only and cannot run on an ARM VPS.
# The official image already has the PGDG apt repo configured, so install PostGIS + pgvector from it.
FROM postgres:18
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-18-postgis-3 postgresql-18-pgvector \
 && rm -rf /var/lib/apt/lists/*
# WAL-G (production only) is added in T4.4.
COPY db/init/00_init.sh /docker-entrypoint-initdb.d/00_init.sh
