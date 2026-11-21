"""One scan.requested job. Order at the end of every job: commit DB -> XADD scan-completed -> XACK.

Steps 1-6 (text analysis, jobs/text_steps.py) run for every job; scoring (steps 7-12) arrives in T2.4.
Until then the job only finishes a scan when WORKER_FAKE_PIPELINE=1, writing a placeholder assessment
with score 0.
"""

import logging
from typing import Any

from psycopg_pool import ConnectionPool

from troviec_worker.jobs import text_steps
from troviec_worker.pipeline.budget import Budget
from troviec_worker.registry import Registry
from troviec_worker.streams import SCAN_COMPLETED, Message, StreamConsumer, publish

log = logging.getLogger(__name__)

FINISHED = ("completed", "failed")
MAX_RETRIES_ERROR = "worker.max_retries"
MODEL_UNAVAILABLE_ERROR = "worker.model_unavailable"


class PipelineNotReadyError(Exception):
    """Raised when the real pipeline is needed but not implemented yet; the job is left un-acked."""


def load_scan(pool: ConnectionPool, scan_id: int) -> dict[str, Any] | None:
    with pool.connection() as conn:
        return conn.execute(
            """
            SELECT id, public_id, source::text AS source, listing_id, status::text AS status
            FROM app.scans WHERE id = %s
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
    message: Message,
    consumer: StreamConsumer,
    pool: ConnectionPool,
    registry: Registry,
    fake_pipeline: bool,
    deps: text_steps.PipelineDeps | None = None,
) -> None:
    """`deps=None` skips the text steps (tests of the queue mechanics only)."""
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

    text_steps.mark_processing(pool, scan_id)
    if deps is not None:
        text_steps.analyse(pool, scan, deps, budget)

    if not fake_pipeline:
        raise PipelineNotReadyError("scoring pipeline not implemented yet (T2.x); set WORKER_FAKE_PIPELINE=1")

    aggregator = registry.active("aggregator")
    if aggregator is None:
        _fail(pool, scan_id, MODEL_UNAVAILABLE_ERROR)
    else:
        _write_fake_assessment(pool, scan_id, aggregator.id)
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


def _write_fake_assessment(pool: ConnectionPool, scan_id: int, aggregator_id: int) -> None:
    with pool.connection() as conn, conn.transaction():
        conn.execute(
            """
            INSERT INTO app.assessments (scan_id, aggregator_model_id, score, risk_level)
            VALUES (%s, %s, 0, 'low')
            ON CONFLICT (scan_id, aggregator_model_id) DO NOTHING
            """,
            (scan_id, aggregator_id),
        )
        conn.execute(
            """
            UPDATE app.scans SET status = 'completed', completed_at = now()
            WHERE id = %s AND status NOT IN ('completed', 'failed')
            """,
            (scan_id,),
        )


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
