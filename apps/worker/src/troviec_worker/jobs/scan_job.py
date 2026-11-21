"""One scan.requested job (docs/05-ai-worker.md). Order at the end: commit DB -> XADD scan-completed -> XACK.

Steps implemented: 1 (load, processing, cache), 2-6 (jobs/text_steps.py), 7 text signal, 11
aggregate, 12 write.
Image (8), price (9) and community (10) signals arrive in later tasks and stay NULL until then.
"""

import logging
from dataclasses import dataclass
from typing import Any

from psycopg_pool import ConnectionPool

from troviec_worker.jobs import scoring, text_steps
from troviec_worker.llm.base import LlmClient
from troviec_worker.pipeline import aggregate, text_signal
from troviec_worker.pipeline.budget import Budget
from troviec_worker.registry import ModelVersion, Registry
from troviec_worker.streams import SCAN_COMPLETED, Message, StreamConsumer, publish

log = logging.getLogger(__name__)

FINISHED = ("completed", "failed")
MAX_RETRIES_ERROR = "worker.max_retries"
MODEL_UNAVAILABLE_ERROR = "worker.model_unavailable"


@dataclass(frozen=True)
class JobDeps:
    text: text_steps.PipelineDeps
    scoring: scoring.ScoringDeps


def load_scan(pool: ConnectionPool, scan_id: int) -> dict[str, Any] | None:
    with pool.connection() as conn:
        return conn.execute(
            """
            SELECT s.id, s.public_id, s.source::text AS source, s.listing_id, s.status::text AS status,
                   l.listing_type::text AS listing_type
            FROM app.scans s LEFT JOIN app.listings l ON l.id = s.listing_id
            WHERE s.id = %s
            """,
            (scan_id,),
        ).fetchone()


def completed_event(scan: dict[str, Any]) -> dict[str, Any]:
    return {
        "scanId": scan["id"],
        "publicId": str(scan["public_id"]),
        "status": scan["status"],
        "source": scan["source"],
        "listingId": scan["listing_id"],
    }


def handle(
    message: Message, consumer: StreamConsumer, pool: ConnectionPool, registry: Registry, deps: JobDeps
) -> None:
    budget = Budget()  # 8 s from the moment the worker takes the job
    scan_id = int(message.payload["scanId"])
    scan = load_scan(pool, scan_id)
    if scan is None:
        # Deleted by the nightly job (quick checks live 30 days); nothing to report.
        log.info("scan missing, acking", extra={"scan_id": scan_id, "message_id": message.id})
        consumer.ack(message.id)
        return

    if scan["status"] in FINISHED:
        # Redelivery after a crash between commit and XADD: re-emit the event so the API is not left waiting.
        _finish(consumer, message, scan, replay=True)
        return

    aggregator = registry.active("aggregator")
    config = _aggregator_config(aggregator)
    if aggregator is None or config is None:
        _fail(pool, scan_id, MODEL_UNAVAILABLE_ERROR)
        _finish(consumer, message, load_scan(pool, scan_id) or scan, replay=False)
        return

    text_steps.mark_processing(pool, scan_id)
    if scan["source"] == "quick_check" and scoring.copy_from_cache(pool, scan, aggregator.id):
        _finish(consumer, message, load_scan(pool, scan_id) or scan, replay=False)
        return

    analysis = text_steps.analyse(pool, scan, deps.text, budget)
    kind = scan["listing_type"] or "unknown"
    criteria = deps.scoring.criteria.for_kind(kind)
    llm, llm_version = _llm_for_this_scan(registry, deps.scoring, consumer)
    outcome = text_signal.compute(
        analysis.masked_text, analysis.injections, criteria, llm, budget.llm_timeout(), kind
    )
    result = aggregate.score(
        config,
        {"text": outcome.signal},
        flag_count=len(outcome.flags),
        has_injection=any(code == text_signal.INJECTION_CODE for code, _ in outcome.flags),
    )
    llm_model_id = llm_version.id if outcome.llm_used and llm_version else None
    scoring.write_result(pool, scan_id, aggregator.id, llm_model_id, outcome, result)
    _finish(consumer, message, load_scan(pool, scan_id) or scan, replay=False)


def dead_letter(message: Message, consumer: StreamConsumer, pool: ConnectionPool, error: str) -> None:
    """Delivered more than max_deliveries times: record it, fail the scan, tell the API, then ack."""
    consumer.dead_letter(message, error)
    scan_id = int(message.payload.get("scanId", 0))
    _fail(pool, scan_id, MAX_RETRIES_ERROR)
    scan = load_scan(pool, scan_id)
    if scan is not None:
        publish(consumer.client, SCAN_COMPLETED, "scan.completed", completed_event(scan))
    consumer.ack(message.id)
    log.warning("dead-lettered", extra={"scan_id": scan_id, "message_id": message.id})


def _aggregator_config(version: ModelVersion | None) -> aggregate.AggregatorConfig | None:
    """No local implementation for the running aggregator version → worker.model_unavailable."""
    if version is None:
        return None
    try:
        return aggregate.load(version.version)
    except (FileNotFoundError, ValueError, KeyError):
        log.error("aggregator version has no implementation", extra={"version": version.version})
        return None


def _llm_for_this_scan(
    registry: Registry, deps: scoring.ScoringDeps, consumer: StreamConsumer
) -> tuple[LlmClient | None, ModelVersion | None]:
    selected = scoring.select_llm(registry, deps.settings)
    if selected is None:
        return None, None
    if not scoring.take_llm_call(consumer.client, deps.settings.llm_daily_call_limit):
        log.warning("daily llm budget used up; using rules")
        return None, None
    return selected


def _fail(pool: ConnectionPool, scan_id: int, error_code: str) -> None:
    with pool.connection() as conn, conn.transaction():
        conn.execute(
            """
            UPDATE app.scans SET status = 'failed', error_code = %s, completed_at = now()
            WHERE id = %s AND status NOT IN ('completed', 'failed')
            """,
            (error_code, scan_id),
        )


def _finish(consumer: StreamConsumer, message: Message, scan: dict[str, Any], replay: bool) -> None:
    publish(consumer.client, SCAN_COMPLETED, "scan.completed", completed_event(scan))
    consumer.ack(message.id)
    log.info(
        "scan finished",
        extra={"scan_id": scan["id"], "status": scan["status"], "replay": replay, "message_id": message.id},
    )
