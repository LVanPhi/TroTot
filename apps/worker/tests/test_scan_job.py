import json

import pytest
import redis
from psycopg_pool import ConnectionPool

from troviec_worker.config import Settings
from troviec_worker.consumer import process
from troviec_worker.jobs import scan_job
from troviec_worker.registry import Registry
from troviec_worker.streams import (
    DEAD_LETTER,
    GROUP,
    SCAN_COMPLETED,
    SCAN_REQUESTED,
    Message,
    StreamConsumer,
    publish,
)


def _settings(fake: bool) -> Settings:
    return Settings(database_url="unused", redis_url="unused", worker_fake_pipeline=fake)


def _deliver(client: redis.Redis, scan_id: int) -> tuple[StreamConsumer, Message]:
    consumer = StreamConsumer(client, SCAN_REQUESTED, "worker-1")
    consumer.ensure_group()
    publish(client, SCAN_REQUESTED, "scan.requested", {"scanId": scan_id})
    message = consumer.read(block_ms=1000)
    assert message is not None
    return consumer, message


def _completed_events(client: redis.Redis) -> list[dict[str, object]]:
    return [json.loads(fields["payload"]) for _, fields in client.xrange(SCAN_COMPLETED)]


def _pending(client: redis.Redis) -> int:
    return int(client.xpending(SCAN_REQUESTED, GROUP)["pending"])


def test_process_fake_pipeline_writes_zero_score_and_completes(
    redis_client: redis.Redis, worker_pool: ConnectionPool, new_scan, sql
) -> None:
    scan_id = new_scan()
    consumer, message = _deliver(redis_client, scan_id)

    process(message, consumer, worker_pool, Registry(worker_pool), _settings(fake=True))

    assert sql("SELECT score, risk_level::text FROM app.assessments WHERE scan_id = %s", (scan_id,)) == [
        (0, "low")
    ]
    status, completed_at = sql("SELECT status::text, completed_at FROM app.scans WHERE id = %s", (scan_id,))[
        0
    ]
    assert status == "completed"
    assert completed_at is not None
    events = _completed_events(redis_client)
    assert [(e["scanId"], e["status"], e["source"], e["listingId"]) for e in events] == [
        (scan_id, "completed", "quick_check", None)
    ]
    assert _pending(redis_client) == 0


@pytest.mark.parametrize("final_status", ["completed", "failed"])
def test_handle_already_finished_scan_replays_event_and_acks(
    redis_client: redis.Redis, worker_pool: ConnectionPool, new_scan, sql, final_status: str
) -> None:
    scan_id = new_scan()
    error_code = "worker.max_retries" if final_status == "failed" else None
    sql(
        "UPDATE app.scans SET status = %s::app.scan_status, error_code = %s, completed_at = now() "
        "WHERE id = %s",
        (final_status, error_code, scan_id),
    )
    consumer, message = _deliver(redis_client, scan_id)

    scan_job.handle(message, consumer, worker_pool, Registry(worker_pool), fake_pipeline=True)

    assert [(e["scanId"], e["status"]) for e in _completed_events(redis_client)] == [(scan_id, final_status)]
    assert sql("SELECT count(*) FROM app.assessments WHERE scan_id = %s", (scan_id,)) == [(0,)]
    assert _pending(redis_client) == 0


def test_handle_redelivered_after_commit_does_not_score_twice(
    redis_client: redis.Redis, worker_pool: ConnectionPool, new_scan, sql
) -> None:
    scan_id = new_scan()
    registry = Registry(worker_pool)
    consumer, first = _deliver(redis_client, scan_id)
    scan_job.handle(first, consumer, worker_pool, registry, fake_pipeline=True)
    _, second = _deliver(redis_client, scan_id)

    scan_job.handle(second, consumer, worker_pool, registry, fake_pipeline=True)

    assert sql("SELECT count(*) FROM app.assessments WHERE scan_id = %s", (scan_id,)) == [(1,)]
    assert [e["status"] for e in _completed_events(redis_client)] == ["completed", "completed"]
    assert _pending(redis_client) == 0


def test_process_without_fake_pipeline_leaves_entry_pending(
    redis_client: redis.Redis, worker_pool: ConnectionPool, new_scan, sql
) -> None:
    scan_id = new_scan()
    consumer, message = _deliver(redis_client, scan_id)

    process(message, consumer, worker_pool, Registry(worker_pool), _settings(fake=False))

    assert sql("SELECT status::text FROM app.scans WHERE id = %s", (scan_id,)) == [("queued",)]
    assert _pending(redis_client) == 1
    assert _completed_events(redis_client) == []


def test_process_after_three_failed_deliveries_dead_letters_and_fails_scan(
    redis_client: redis.Redis, worker_pool: ConnectionPool, new_scan, sql
) -> None:
    scan_id = new_scan()
    settings = _settings(fake=False)  # pipeline "fails" every time: entry stays pending
    registry = Registry(worker_pool)
    consumer, message = _deliver(redis_client, scan_id)
    process(message, consumer, worker_pool, registry, settings)  # delivery 1
    for _ in range(2):  # deliveries 2 and 3
        (message,) = consumer.reclaim(min_idle_ms=0)
        process(message, consumer, worker_pool, registry, settings)
    assert _pending(redis_client) == 1

    (message,) = consumer.reclaim(min_idle_ms=0)  # delivery 4 > 3
    process(message, consumer, worker_pool, registry, settings)

    status, error_code, completed_at = sql(
        "SELECT status::text, error_code, completed_at FROM app.scans WHERE id = %s", (scan_id,)
    )[0]
    assert (status, error_code) == ("failed", "worker.max_retries")
    assert completed_at is not None
    dead = [json.loads(fields["payload"]) for _, fields in redis_client.xrange(DEAD_LETTER)]
    assert [(d["stream"], d["payload"], d["deliveries"]) for d in dead] == [
        (SCAN_REQUESTED, {"scanId": scan_id}, 4)
    ]
    assert [(e["scanId"], e["status"]) for e in _completed_events(redis_client)] == [(scan_id, "failed")]
    assert _pending(redis_client) == 0


def test_handle_missing_scan_acks_without_event(
    redis_client: redis.Redis, worker_pool: ConnectionPool
) -> None:
    consumer, message = _deliver(redis_client, 999_999_999)

    scan_job.handle(message, consumer, worker_pool, Registry(worker_pool), fake_pipeline=True)

    assert _completed_events(redis_client) == []
    assert _pending(redis_client) == 0
