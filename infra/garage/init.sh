#!/usr/bin/env bash
# Idempotent one-node Garage setup for dev: layout, 3 buckets, 2 keys, website mode.
# Run from repo root after the stack is up. Prints keys; copy them into .env yourself.
# Production: GARAGE_COMPOSE_FILE=docker-compose.prod.yml GARAGE_ENV_FILE=.env bash garage/init.sh (from /opt/troviec).
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash on Windows: keep "/garage" as a container path

COMPOSE=(docker compose -f "${GARAGE_COMPOSE_FILE:-infra/docker-compose.yml}" --env-file "${GARAGE_ENV_FILE:-.env}")
garage() { "${COMPOSE[@]}" exec -T -e RUST_LOG=warn garage /garage "$@"; }

ALL_BUCKETS=(listing-images scan-images owner-documents)
WORKER_BUCKETS=(listing-images scan-images)

# Wait until the Garage daemon answers.
for _ in $(seq 1 30); do garage status >/dev/null 2>&1 && break; sleep 1; done

if garage layout show | grep -q "Current cluster layout version: 0"; then
  node_id=$(garage node id -q | cut -d@ -f1)
  garage layout assign -z dc1 -c 20G "$node_id" >/dev/null
  garage layout apply --version 1 >/dev/null
fi

for b in "${ALL_BUCKETS[@]}"; do
  garage bucket info "$b" >/dev/null 2>&1 || garage bucket create "$b" >/dev/null
done

ensure_key() { garage key info "$1" >/dev/null 2>&1 || garage key create "$1" >/dev/null; }
ensure_key troviec-app
ensure_key troviec-worker

for b in "${ALL_BUCKETS[@]}"; do garage bucket allow --read --write "$b" --key troviec-app >/dev/null; done
for b in "${WORKER_BUCKETS[@]}"; do garage bucket allow --read "$b" --key troviec-worker >/dev/null; done
garage bucket website --allow listing-images >/dev/null

print_key() {
  local info id secret
  info=$(garage key info --show-secret "$1")
  id=$(grep -i "key id" <<<"$info" | awk '{print $NF}')
  secret=$(grep -i "secret key" <<<"$info" | awk '{print $NF}')
  echo "$2=$id"
  echo "$3=$secret"
}
echo "# Garage ready. Put these into .env:"
print_key troviec-app S3__AccessKey S3__SecretKey
print_key troviec-worker S3_ACCESS_KEY S3_SECRET_KEY
