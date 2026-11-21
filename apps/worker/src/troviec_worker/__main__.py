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


def _serve_embed(settings: Settings) -> None:
    import uvicorn

    from troviec_worker.embed_api import create_app

    uvicorn.run(create_app(settings.internal_api_token), host="0.0.0.0", port=8001, log_config=None)  # noqa: S104


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
        log.warning("embed-backfill is not implemented yet (T3.x)")


if __name__ == "__main__":
    main()
