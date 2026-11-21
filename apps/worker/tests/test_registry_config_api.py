from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from psycopg_pool import ConnectionPool

from troviec_worker.config import Settings, find_repo_env
from troviec_worker.embed_api import create_app
from troviec_worker.registry import Registry, fetch_active


def test_fetch_active_seed_has_four_running_components(worker_pool: ConnectionPool) -> None:
    active = fetch_active(worker_pool)

    assert sorted(active) == ["aggregator", "image_embedding", "ocr", "text_embedding"]
    assert (active["aggregator"].name, active["aggregator"].version) == ("weighted-rules", "agg-v0")


def test_registry_reloads_only_after_refresh_interval(worker_pool: ConnectionPool, sql) -> None:
    now = [0.0]
    registry = Registry(worker_pool, refresh_seconds=60, clock=lambda: now[0])
    assert registry.active("llm") is None

    sql(
        "INSERT INTO app.model_versions (component, name, version, is_active, activated_at) "
        "VALUES ('llm', 'fake/deterministic', 'prompt-v1', true, now())"
    )
    try:
        now[0] = 59
        assert registry.active("llm") is None  # cached
        now[0] = 60
        llm = registry.active("llm")
        assert llm is not None
        assert llm.name == "fake/deterministic"
    finally:
        sql("DELETE FROM app.model_versions WHERE component = 'llm'")


def test_settings_read_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("DATABASE_URL", "postgresql://x")
    monkeypatch.setenv("REDIS_URL", "redis://y")
    monkeypatch.setenv("WORKER_FAKE_PIPELINE", "1")

    settings = Settings(_env_file=None)  # type: ignore[call-arg]

    assert (settings.database_url, settings.redis_url, settings.worker_fake_pipeline) == (
        "postgresql://x",
        "redis://y",
        True,
    )
    assert settings.max_deliveries == 3


def test_find_repo_env_without_env_file_returns_none(tmp_path: Path) -> None:
    (tmp_path / "AGENTS.md").write_text("x")

    assert find_repo_env(tmp_path) is None


@pytest.fixture
def client() -> TestClient:
    return TestClient(create_app("secret-token"))


def test_embed_wrong_token_returns_401(client: TestClient) -> None:
    response = client.post("/internal/embed", json={"texts": ["a"]}, headers={"X-Internal-Token": "nope"})
    assert response.status_code == 401


def test_embed_valid_request_returns_503_until_model_exists(client: TestClient) -> None:
    response = client.post(
        "/internal/embed", json={"texts": ["a"]}, headers={"X-Internal-Token": "secret-token"}
    )
    assert response.status_code == 503


@pytest.mark.parametrize("texts", [[], ["a"] * 17, ["x" * 2001]])
def test_embed_invalid_body_returns_422(client: TestClient, texts: list[str]) -> None:
    response = client.post(
        "/internal/embed", json={"texts": texts}, headers={"X-Internal-Token": "secret-token"}
    )
    assert response.status_code == 422
