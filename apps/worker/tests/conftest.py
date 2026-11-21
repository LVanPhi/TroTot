"""Real Redis and PostgreSQL for tests.

PostgreSQL uses the same image as dev/prod (infra/postgres/Dockerfile), initialised by db/init/00_init.sh,
with db/migrations applied in order as app_migrator (same order TroViec.Migrator uses).
"""

import subprocess
from collections.abc import Iterator
from pathlib import Path

import psycopg
import pytest
import redis
from psycopg_pool import ConnectionPool
from testcontainers.community.postgres import PostgresContainer
from testcontainers.community.redis import RedisContainer

from troviec_worker.db import make_pool

REPO_ROOT = next(p for p in Path(__file__).resolve().parents if (p / "AGENTS.md").exists())
PG_IMAGE = "troviec-postgres-test:latest"
PASSWORDS = {
    "APP_MIGRATOR_PASSWORD": "test-migrator",
    "APP_API_PASSWORD": "test-api",
    "APP_WORKER_PASSWORD": "test-worker",
    "APP_MAINTENANCE_PASSWORD": "test-maintenance",
    "APP_REPORTING_PASSWORD": "test-reporting",
    "APP_RESEARCH_PASSWORD": "test-research",
}


def _url(host: str, port: int, user: str, password: str) -> str:
    return f"postgresql://{user}:{password}@{host}:{port}/troviec"


@pytest.fixture(scope="session")
def postgres() -> Iterator[dict[str, str]]:
    subprocess.run(
        ["docker", "build", "-q", "-t", PG_IMAGE, "-f", "infra/postgres/Dockerfile", "."],
        cwd=REPO_ROOT,
        check=True,
        capture_output=True,
    )
    container = PostgresContainer(PG_IMAGE, username="postgres", password="test-superuser", dbname="troviec")
    for key, value in PASSWORDS.items():
        container.with_env(key, value)
    with container:
        host, port = container.get_container_host_ip(), int(container.get_exposed_port(5432))
        migrator = _url(host, port, "app_migrator", PASSWORDS["APP_MIGRATOR_PASSWORD"])
        with psycopg.connect(migrator, autocommit=True) as conn:
            for script in sorted((REPO_ROOT / "db" / "migrations").glob("*.sql")):
                conn.execute(script.read_text(encoding="utf-8"))  # type: ignore[arg-type]
        yield {
            "migrator": migrator,
            "worker": _url(host, port, "app_worker", PASSWORDS["APP_WORKER_PASSWORD"]),
        }


@pytest.fixture(scope="session")
def worker_pool(postgres: dict[str, str]) -> Iterator[ConnectionPool]:
    pool = make_pool(postgres["worker"])
    yield pool
    pool.close()


@pytest.fixture(scope="session")
def redis_url() -> Iterator[str]:
    with RedisContainer("redis:8-alpine") as container:
        yield f"redis://{container.get_container_host_ip()}:{container.get_exposed_port(6379)}/0"


@pytest.fixture
def redis_client(redis_url: str) -> Iterator[redis.Redis]:
    client = redis.Redis.from_url(redis_url, decode_responses=True)
    client.flushall()
    yield client
    client.close()


@pytest.fixture
def new_scan(postgres: dict[str, str]):  # noqa: ANN201
    """Insert a queued quick_check scan as app_migrator (the worker role cannot insert scans)."""

    def create() -> int:
        with psycopg.connect(postgres["migrator"], autocommit=True) as conn:
            row = conn.execute(
                """
                INSERT INTO app.scans (source, input_text, input_sha256, expires_at)
                VALUES ('quick_check', 'test', sha256(convert_to(gen_random_uuid()::text, 'UTF8')),
                        now() + interval '30 days')
                RETURNING id
                """
            ).fetchone()
            assert row is not None
            return int(row[0])

    return create


@pytest.fixture
def sql(postgres: dict[str, str]):  # noqa: ANN201
    """Run one statement as app_migrator and return all rows (for assertions and setup)."""

    def run(query: str, params: tuple[object, ...] = ()) -> list[tuple[object, ...]]:
        with psycopg.connect(postgres["migrator"], autocommit=True) as conn:
            cursor = conn.execute(query, params)  # type: ignore[arg-type]
            return cursor.fetchall() if cursor.description else []

    return run
