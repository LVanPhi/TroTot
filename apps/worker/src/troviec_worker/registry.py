"""Active model versions from app.model_versions, reloaded every 60 s.

See docs/05-ai-worker.md, Sổ đăng ký mô hình.
"""

import time
from collections.abc import Callable

from psycopg_pool import ConnectionPool
from pydantic import BaseModel


class ModelVersion(BaseModel):
    id: int
    component: str
    name: str
    version: str


def fetch_active(pool: ConnectionPool) -> dict[str, ModelVersion]:
    with pool.connection() as conn:
        rows = conn.execute(
            "SELECT id, component::text AS component, name, version FROM app.model_versions WHERE is_active"
        ).fetchall()
    return {row["component"]: ModelVersion(**row) for row in rows}


class Registry:
    def __init__(
        self,
        pool: ConnectionPool,
        refresh_seconds: float = 60,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._pool = pool
        self._refresh_seconds = refresh_seconds
        self._clock = clock
        self._loaded_at: float | None = None
        self._active: dict[str, ModelVersion] = {}

    def active(self, component: str) -> ModelVersion | None:
        """The running version of a component, or None when nothing is active (e.g. no LLM)."""
        now = self._clock()
        if self._loaded_at is None or now - self._loaded_at >= self._refresh_seconds:
            self._active = fetch_active(self._pool)
            self._loaded_at = now
        return self._active.get(component)
