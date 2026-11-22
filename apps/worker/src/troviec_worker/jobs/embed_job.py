"""listing.embed jobs and the embed-backfill command (docs/05-ai-worker.md, Embedding).

The listing text is title + description + one structured summary sentence; the vector is stored with the
model_version_id of the running text_embedding version, so search never mixes two models.
"""

import logging
import threading

import redis
from psycopg_pool import ConnectionPool

from troviec_worker.ml.text_embed import MODEL_NAME, MODEL_VERSION, TextEmbedder
from troviec_worker.pipeline.image_signal import vector_literal
from troviec_worker.registry import Registry
from troviec_worker.streams import LISTING_EMBED, Message, StreamConsumer

log = logging.getLogger(__name__)

PAY_UNITS = {"hour": "giờ", "shift": "buổi", "day": "ngày", "month": "tháng"}


def _vnd(amount: int) -> str:
    return f"{amount:,}".replace(",", ".") + " đồng"


def listing_text(row: dict) -> str:
    """Title, description and a summary sentence.

    Example summary: 'Phòng khép kín 20 m², 2.500.000 đồng/tháng, tiện nghi: wifi.'
    """
    if row["listing_type"] == "room":
        kind = "Phòng khép kín" if row["is_self_contained"] else "Phòng"
        area = f"{float(row['area_m2']):g}".replace(".", ",")
        summary = f"{kind} {area} m², {_vnd(row['monthly_rent_vnd'])}/tháng"
        if row["amenities"]:
            summary += ", tiện nghi: " + ", ".join(a.lower() for a in row["amenities"])
    else:
        summary = (
            f"Việc làm thêm tại {row['employer_name']}, {_vnd(row['pay_vnd'])}/{PAY_UNITS[row['pay_unit']]}, "
            f"{row['hours_per_week']} giờ/tuần"
        )
    return f"{row['title']}\n{row['description']}\n{summary}."[:2000]


def model_version_id(registry: Registry) -> int | None:
    """Id of the running text_embedding version when this worker implements it; None otherwise."""
    version = registry.active("text_embedding")
    if version is None or (version.name, version.version) != (MODEL_NAME, MODEL_VERSION):
        return None
    return version.id


LISTING_SQL = """
SELECT l.id, l.listing_type::text AS listing_type, l.title, l.description,
       r.monthly_rent_vnd, r.area_m2, r.is_self_contained,
       j.employer_name, j.pay_vnd, j.pay_unit::text AS pay_unit, j.hours_per_week,
       coalesce((SELECT array_agg(a.name ORDER BY a.name) FROM app.room_amenities ra
                 JOIN app.amenities a ON a.id = ra.amenity_id WHERE ra.listing_id = l.id), '{}') AS amenities
FROM app.listings l
LEFT JOIN app.room_listings r ON r.listing_id = l.id
LEFT JOIN app.job_listings j ON j.listing_id = l.id
WHERE l.id = %s
"""


def embed_listing(pool: ConnectionPool, embedder: TextEmbedder, listing_id: int, version_id: int) -> bool:
    """Upsert the listing's vector; False when the listing no longer exists."""
    with pool.connection() as conn:
        row = conn.execute(LISTING_SQL, (listing_id,)).fetchone()
    if row is None:
        return False
    (vector,) = embedder.embed([listing_text(row)])
    with pool.connection() as conn, conn.transaction():
        conn.execute(
            """
            INSERT INTO app.listing_embeddings (listing_id, embedding, model_version_id)
            VALUES (%s, %s::vector, %s)
            ON CONFLICT (listing_id) DO UPDATE
                SET embedding = EXCLUDED.embedding, model_version_id = EXCLUDED.model_version_id,
                    updated_at = now()
            """,
            (listing_id, vector_literal(vector), version_id),
        )
    return True


def handle(
    message: Message,
    consumer: StreamConsumer,
    pool: ConnectionPool,
    registry: Registry,
    embedder: TextEmbedder | None,
) -> None:
    """Without the model (or with another active version) the job is acknowledged; backfill catches up
    later."""
    listing_id = int(message.payload.get("listingId", 0))
    version_id = model_version_id(registry)
    if embedder is None or version_id is None:
        log.warning(
            "listing.embed skipped: no matching text_embedding model", extra={"listing_id": listing_id}
        )
    else:
        embed_listing(pool, embedder, listing_id, version_id)
        log.info("listing embedded", extra={"listing_id": listing_id})
    consumer.ack(message.id)


def run(
    client: redis.Redis,
    pool: ConnectionPool,
    embedder: TextEmbedder | None,
    stop: threading.Event,
    block_ms: int = 5000,
) -> None:
    consumer = StreamConsumer(client, LISTING_EMBED)
    consumer.ensure_group()
    registry = Registry(pool)
    while not stop.is_set():
        try:
            for message in consumer.reclaim(120_000):
                handle(message, consumer, pool, registry, embedder)
            message = consumer.read(block_ms)
            if message is not None:
                handle(message, consumer, pool, registry, embedder)
        except (redis.ConnectionError, redis.TimeoutError):
            log.warning("redis unavailable, retrying in 2 s")
            stop.wait(2)
        except Exception:  # noqa: BLE001 - keep serving; the entry stays pending and is reclaimed later
            log.exception("listing.embed failed")
            stop.wait(1)


def backfill(pool: ConnectionPool, embedder: TextEmbedder, registry: Registry) -> int:
    """Embeds every verified/expired listing without a vector of the running model. Returns the count."""
    version_id = model_version_id(registry)
    if version_id is None:
        raise RuntimeError("no running text_embedding version implemented by this worker")
    with pool.connection() as conn:
        rows = conn.execute(
            """
            SELECT l.id FROM app.listings l LEFT JOIN app.listing_embeddings e ON e.listing_id = l.id
            WHERE l.status IN ('verified', 'expired') AND (e.listing_id IS NULL OR e.model_version_id <> %s)
            ORDER BY l.id
            """,
            (version_id,),
        ).fetchall()
    for row in rows:
        embed_listing(pool, embedder, row["id"], version_id)
    log.info("backfill done", extra={"listings": len(rows)})
    return len(rows)
