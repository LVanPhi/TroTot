"""python -m troviec_worker consume | serve-embed | embed-backfill"""

import argparse
import logging
import signal
import threading

from troviec_worker import consumer, logs
from troviec_worker.config import Settings
from troviec_worker.db import make_pool
from troviec_worker.streams import make_client

log = logging.getLogger("troviec_worker")


def _consume(settings: Settings) -> None:
    stop = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: stop.set())
    client = make_client(settings.redis_url, settings.read_block_ms)
    pool = make_pool(settings.database_url)
    try:
        consumer.run(settings, client, pool, stop)
    finally:
        pool.close()
        client.close()


def _embedder(settings: Settings):  # noqa: ANN202 - TextEmbedder | None
    from troviec_worker.ml import text_embed

    if not text_embed.available():
        log.warning("sentence-transformers not installed: /internal/embed answers 503")
        return None
    return text_embed.BgeM3Embedder(settings.model_dir)


def _serve_embed(settings: Settings) -> None:
    """HTTP embedding for search queries, plus the listing.embed consumer in a background thread."""
    import uvicorn

    from troviec_worker.embed_api import create_app
    from troviec_worker.jobs import embed_job
    from troviec_worker.registry import Registry

    embedder = _embedder(settings)
    pool = make_pool(settings.database_url)
    registry = Registry(pool)
    client = make_client(settings.redis_url, settings.read_block_ms)
    stop = threading.Event()
    worker = threading.Thread(
        target=embed_job.run, args=(client, pool, embedder, stop, settings.read_block_ms), daemon=True
    )
    worker.start()
    try:
        app = create_app(settings.internal_api_token, embedder, lambda: embed_job.model_version_id(registry))
        uvicorn.run(app, host="0.0.0.0", port=8001, log_config=None)  # noqa: S104 - internal Docker network
    finally:
        stop.set()
        pool.close()
        client.close()


def _embed_backfill(settings: Settings) -> None:
    from troviec_worker.jobs import embed_job
    from troviec_worker.registry import Registry

    embedder = _embedder(settings)
    if embedder is None:
        raise SystemExit("embed-backfill needs the ml extra (sentence-transformers)")
    pool = make_pool(settings.database_url)
    try:
        embed_job.backfill(pool, embedder, Registry(pool))
    finally:
        pool.close()


def main() -> None:
    parser = argparse.ArgumentParser(prog="troviec_worker")
    parser.add_argument("command", choices=["consume", "serve-embed", "embed-backfill"])
    args = parser.parse_args()

    settings = Settings()  # type: ignore[call-arg]  # required values come from the environment
    logs.configure(settings.log_level)

    if args.command == "consume":
        _consume(settings)
    elif args.command == "serve-embed":
        _serve_embed(settings)
    else:
        _embed_backfill(settings)


if __name__ == "__main__":
    main()
