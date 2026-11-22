"""`consume` loop: read troviec:scan-requested, reclaim stuck jobs every 60 s.

Entries delivered more than 3 times go to the dead letter stream.
"""

import logging
import threading
import time

import redis
from psycopg_pool import ConnectionPool

from troviec_worker.config import Settings
from troviec_worker.jobs import scan_job, text_steps
from troviec_worker.pipeline import identifiers, pii
from troviec_worker.registry import Registry
from troviec_worker.storage import make_image_loader
from troviec_worker.streams import SCAN_REQUESTED, Message, StreamConsumer

log = logging.getLogger(__name__)


def process(
    message: Message,
    consumer: StreamConsumer,
    pool: ConnectionPool,
    registry: Registry,
    settings: Settings,
    deps: text_steps.PipelineDeps | None = None,
) -> None:
    """Run one job. Any failure leaves the entry pending so XAUTOCLAIM redelivers it later."""
    if message.deliveries > settings.max_deliveries:
        scan_job.dead_letter(message, consumer, pool, error="max deliveries exceeded")
        return
    try:
        scan_job.handle(message, consumer, pool, registry, settings.worker_fake_pipeline, deps)
    except scan_job.PipelineNotReadyError:
        log.warning("pipeline not ready, leaving pending", extra={"message_id": message.id})
    except Exception:
        # Type only: messages may carry user content.
        log.exception("job failed, will be redelivered", extra={"message_id": message.id})


def build_deps(settings: Settings) -> text_steps.PipelineDeps:
    """S3 loader, HMAC key (base64-decoded IDENTIFIER_HMAC_KEY) and optional NER for the text steps."""
    if not settings.identifier_hmac_key:
        raise RuntimeError("IDENTIFIER_HMAC_KEY is required (base64 of 32 random bytes, same as the API)")
    return text_steps.PipelineDeps(
        load_image=make_image_loader(settings),
        hmac_key=identifiers.decode_key(settings.identifier_hmac_key),
        scan_bucket=settings.s3_bucket_scan_images,
        ner=_optional_ner(),
    )


def _optional_ner() -> pii.NerFn | None:
    """underthesea NER when the `ml` extra is installed (Docker image); regex-only masking otherwise."""
    try:
        import underthesea  # type: ignore[import-not-found]  # noqa: F401
    except ImportError:
        log.info("underthesea not installed: name masking uses the regex layer only")
        return None
    return pii.underthesea_people


def run(
    settings: Settings,
    client: redis.Redis,
    pool: ConnectionPool,
    stop: threading.Event,
) -> None:
    consumer = StreamConsumer(client, SCAN_REQUESTED)
    consumer.ensure_group()
    registry = Registry(pool)
    deps = build_deps(settings)
    log.info("consuming", extra={"stream": SCAN_REQUESTED, "consumer": consumer.consumer})

    next_reclaim = 0.0
    while not stop.is_set():
        try:
            if time.monotonic() >= next_reclaim:
                for message in consumer.reclaim(settings.reclaim_min_idle_ms):
                    process(message, consumer, pool, registry, settings, deps)
                next_reclaim = time.monotonic() + settings.reclaim_every_seconds

            message = consumer.read(settings.read_block_ms)
            if message is not None:
                process(message, consumer, pool, registry, settings, deps)
        except (redis.ConnectionError, redis.TimeoutError):
            # Redis restarting: wait and retry; un-acked entries are reclaimed later.
            log.warning("redis unavailable, retrying in 2 s")
            stop.wait(2)
    log.info("stopped")
