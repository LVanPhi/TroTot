"""Steps 1 (cache), 7, 11 and 12 of a scan (docs/05-ai-worker.md): pick the text signal source,
score, write."""

import logging
import time
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime
from zoneinfo import ZoneInfo

import redis
from psycopg_pool import ConnectionPool

from troviec_worker.config import Settings
from troviec_worker.llm import prompt
from troviec_worker.llm.base import LlmClient
from troviec_worker.llm.fake import FakeLlmClient
from troviec_worker.llm.openai_compat import OpenAiCompatClient
from troviec_worker.pipeline.aggregate import AggregateResult
from troviec_worker.pipeline.text_signal import TextOutcome
from troviec_worker.registry import ModelVersion, Registry

log = logging.getLogger(__name__)

VIETNAM = ZoneInfo("Asia/Ho_Chi_Minh")
CACHE_DAYS = 7


class CriteriaCache:
    """Active criteria, reloaded every 60 s so admin edits apply without a deploy."""

    def __init__(
        self, pool: ConnectionPool, ttl: float = 60, clock: Callable[[], float] = time.monotonic
    ) -> None:
        self._pool, self._ttl, self._clock = pool, ttl, clock
        self._loaded_at: float | None = None
        self._rows: list[prompt.Criterion] = []

    def for_kind(self, kind: str) -> list[prompt.Criterion]:
        """Criteria whose scope matches the listing kind; quick checks ("unknown") get all of them."""
        now = self._clock()
        if self._loaded_at is None or now - self._loaded_at >= self._ttl:
            with self._pool.connection() as conn:
                rows = conn.execute(
                    """
                    SELECT id, code, name, guidance, scope::text AS scope
                    FROM app.criteria WHERE is_active ORDER BY id
                    """
                ).fetchall()
            self._rows = [prompt.Criterion(**row) for row in rows]
            self._loaded_at = now
        return [c for c in self._rows if kind == "unknown" or c.scope in (kind, "both")]


def select_llm(registry: Registry, settings: Settings) -> tuple[LlmClient, ModelVersion] | None:
    """The running `llm` version if it can really be used here; otherwise None (rules only, logged)."""
    version = registry.active("llm")
    if version is None:
        return None
    if version.name == "fake/deterministic":
        if settings.llm_fake:
            return FakeLlmClient(), version
        log.warning("llm fake/deterministic is active but LLM_FAKE is not 1; using rules")
        return None
    prefix = "openai-compatible/"
    if version.name.startswith(prefix):
        model = version.name.removeprefix(prefix)
        if settings.llm_api_key and settings.llm_base_url and settings.llm_model == model:
            return OpenAiCompatClient(settings.llm_base_url, settings.llm_api_key, model), version
        log.warning("llm model_versions entry does not match LLM_* settings; using rules")
        return None
    log.warning("no implementation for the running llm version; using rules")
    return None


def take_llm_call(client: redis.Redis, daily_limit: int, now: datetime | None = None) -> bool:
    """Counts one call in llm:calls:{yyyyMMdd} (Vietnam day); False once the daily limit is used up."""
    day = (now or datetime.now(UTC)).astimezone(VIETNAM).strftime("%Y%m%d")
    key = f"llm:calls:{day}"
    try:
        used = client.incr(key)
        if used == 1:
            client.expire(key, 2 * 86_400)
    except redis.RedisError:
        return False
    return used <= daily_limit


def copy_from_cache(pool: ConnectionPool, scan: dict, aggregator_id: int) -> bool:
    """Quick checks only: reuse a completed quick check with the same input_sha256 from the last 7
    days that was
    scored by the running aggregator. Copies assessment, flags, image matches, scan_identifiers and
    ocr_text."""
    with pool.connection() as conn, conn.transaction():
        source = conn.execute(
            """
            SELECT o.id AS scan_id, a.id AS assessment_id
            FROM app.scans s
            JOIN app.scans o ON o.input_sha256 = s.input_sha256 AND o.id <> s.id
            JOIN app.assessments a ON a.scan_id = o.id AND a.aggregator_model_id = %s
            WHERE s.id = %s AND s.source = 'quick_check' AND o.source = 'quick_check'
              AND o.status = 'completed' AND o.completed_at > now() - make_interval(days => %s)
            ORDER BY o.completed_at DESC
            LIMIT 1
            """,
            (aggregator_id, scan["id"], CACHE_DAYS),
        ).fetchone()
        if source is None:
            return False
        row = conn.execute(
            """
            INSERT INTO app.assessments (scan_id, aggregator_model_id, llm_model_id, score,
            risk_level, text_signal,
                image_signal, price_signal, community_signal, price_median_vnd, price_sample_size, price_vnd)
            SELECT %s, aggregator_model_id, llm_model_id, score, risk_level, text_signal,
            image_signal, price_signal,
                   community_signal, price_median_vnd, price_sample_size, price_vnd
            FROM app.assessments WHERE id = %s
            ON CONFLICT (scan_id, aggregator_model_id) DO NOTHING
            RETURNING id
            """,
            (scan["id"], source["assessment_id"]),
        ).fetchone()
        if row is not None:
            conn.execute(
                """
                INSERT INTO app.assessment_flags (assessment_id, criterion_id, evidence_text)
                SELECT %s, criterion_id, evidence_text FROM app.assessment_flags WHERE assessment_id = %s
                """,
                (row["id"], source["assessment_id"]),
            )
            conn.execute(
                """
                INSERT INTO app.assessment_image_matches
                    (assessment_id, listing_image_id, source_image_no, hamming_distance, clip_similarity)
                SELECT %s, listing_image_id, source_image_no, hamming_distance, clip_similarity
                FROM app.assessment_image_matches WHERE assessment_id = %s
                """,
                (row["id"], source["assessment_id"]),
            )
        conn.execute(
            """
            INSERT INTO app.scan_identifiers (scan_id, identifier_id)
            SELECT %s, identifier_id FROM app.scan_identifiers WHERE scan_id = %s
            ON CONFLICT DO NOTHING
            """,
            (scan["id"], source["scan_id"]),
        )
        conn.execute(
            """
            UPDATE app.scan_images t SET ocr_text = o.ocr_text
            FROM app.scan_images o
            WHERE t.scan_id = %s AND o.scan_id = %s AND o.image_no = t.image_no
            """,
            (scan["id"], source["scan_id"]),
        )
        _complete(conn, scan["id"])
    log.info("cache hit", extra={"scan_id": scan["id"], "source_scan_id": source["scan_id"]})
    return True


def write_result(
    pool: ConnectionPool,
    scan_id: int,
    aggregator_id: int,
    llm_model_id: int | None,
    text: TextOutcome,
    result: AggregateResult,
) -> None:
    """Step 12: one transaction; an existing assessment for this aggregator means already scored
    (redelivery)."""
    with pool.connection() as conn, conn.transaction():
        row = conn.execute(
            """
            INSERT INTO app.assessments (scan_id, aggregator_model_id, llm_model_id, score,
            risk_level, text_signal)
            VALUES (%s, %s, %s, %s, %s::app.risk_level, %s)
            ON CONFLICT (scan_id, aggregator_model_id) DO NOTHING
            RETURNING id
            """,
            (scan_id, aggregator_id, llm_model_id, result.score, result.risk_level, text.signal),
        ).fetchone()
        if row is not None:
            for code, evidence in text.flags:
                conn.execute(
                    """
                    INSERT INTO app.assessment_flags (assessment_id, criterion_id, evidence_text)
                    SELECT %s, id, %s FROM app.criteria WHERE code = %s
                    """,
                    (row["id"], evidence[:300], code),
                )
        _complete(conn, scan_id)


def _complete(conn: object, scan_id: int) -> None:
    conn.execute(  # type: ignore[attr-defined]
        "UPDATE app.scans SET status = 'completed', completed_at = now() "
        "WHERE id = %s AND status NOT IN ('completed', 'failed')",
        (scan_id,),
    )


@dataclass(frozen=True)
class ScoringDeps:
    settings: Settings
    criteria: CriteriaCache
