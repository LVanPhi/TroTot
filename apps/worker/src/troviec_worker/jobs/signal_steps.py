"""Steps 8-10 of a scan (docs/05-ai-worker.md): image, price and community signals.  Reads what each signal
needs, computes it with the pure functions in pipeline/, and returns the result for step 12 to write in
one transaction. The only write here is filling listing_images.phash / clip_embedding for a listing scan
(the listing's own photos become searchable). Logs carry ids and counts only.
"""

import logging
from dataclasses import dataclass, field
from datetime import UTC, datetime

from psycopg_pool import ConnectionPool

from troviec_worker.jobs.text_steps import TextAnalysis
from troviec_worker.ml.clip import ImageEmbedder
from troviec_worker.pipeline import community_signal, image_signal, price
from troviec_worker.pipeline.image_signal import Candidate
from troviec_worker.pipeline.price import PriceSignal
from troviec_worker.storage import ImageLoader

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class SignalDeps:
    load_image: ImageLoader
    listing_bucket: str = "listing-images"
    clip: ImageEmbedder | None = None  # None: pHash only (no ml extra)


@dataclass
class OtherSignals:
    image: float | None = None
    price: PriceSignal | None = None
    community: float | None = None
    matches: list[Candidate] = field(default_factory=list)

    def as_dict(self) -> dict[str, float | None]:
        return {
            "image": self.image,
            "price": self.price.signal if self.price else None,
            "community": self.community,
        }


def compute(
    pool: ConnectionPool,
    scan: dict,
    analysis: TextAnalysis,
    deps: SignalDeps,
    clip_model_id: int | None,
    now: datetime | None = None,
) -> OtherSignals:
    """clip_model_id: id of the running image_embedding version; None disables CLIP for this scan."""
    with pool.connection() as conn:
        listing = _listing_facts(conn, scan["listing_id"]) if scan["source"] == "listing" else None
    poster_id = listing["poster_id"] if listing else None

    images = _listing_images(pool, scan, deps, clip_model_id) if listing else analysis.images
    embedder = deps.clip if clip_model_id is not None else None
    matches = _image_matches(pool, scan, images, embedder, clip_model_id)
    image = image_signal.signal(matches, len(images), scan["source"], poster_id)

    price_result = _price(pool, scan, analysis, listing)

    with pool.connection() as conn:
        reports, identifier_count = community_signal.load_counts(conn, scan["id"])
    community = community_signal.compute(
        reports,
        identifier_count,
        scan["source"],
        listing["poster_created_at"] if listing else None,
        now or datetime.now(UTC),
    )
    log.info(
        "signal steps done",
        extra={
            "scan_id": scan["id"],
            "images": len(images),
            "image_matches": len(matches),
            "price_samples": price_result.sample_size if price_result else 0,
            "identifiers": identifier_count,
        },
    )
    return OtherSignals(image, price_result, community, matches)


def _listing_facts(conn: object, listing_id: int) -> dict | None:
    return conn.execute(  # type: ignore[attr-defined]
        """
        SELECT l.id, l.poster_id, l.listing_type::text AS listing_type, u.created_at AS poster_created_at,
               r.monthly_rent_vnd, r.is_self_contained
        FROM app.listings l
        JOIN app.users u ON u.id = l.poster_id
        LEFT JOIN app.room_listings r ON r.listing_id = l.id
        WHERE l.id = %s
        """,
        (listing_id,),
    ).fetchone()


def _listing_images(
    pool: ConnectionPool, scan: dict, deps: SignalDeps, clip_model_id: int | None
) -> list[tuple[int, bytes]]:
    with pool.connection() as conn:
        rows = conn.execute(
            """
            SELECT sort_order, object_key FROM app.listing_images
            WHERE listing_id = %s ORDER BY sort_order LIMIT 10
            """,
            (scan["listing_id"],),
        ).fetchall()
    loaded: list[tuple[int, bytes]] = []
    for row in rows:
        try:
            loaded.append((row["sort_order"], deps.load_image(deps.listing_bucket, row["object_key"])))
        except Exception:  # noqa: BLE001 - one missing photo loses that photo only
            log.warning("listing image load failed", extra={"scan_id": scan["id"]}, exc_info=True)
    return loaded


def _image_matches(
    pool: ConnectionPool,
    scan: dict,
    images: list[tuple[int, bytes]],
    embedder: ImageEmbedder | None,
    clip_model_id: int | None,
) -> list[Candidate]:
    if not images:
        return []
    hashes = [image_signal.phash(blob) for _, blob in images]
    vectors: list[list[float] | None] = [None] * len(images)
    if embedder is not None:
        try:
            vectors = list(embedder.embed([blob for _, blob in images]))
        except Exception:  # noqa: BLE001 - CLIP failing must not fail the scan; pHash still runs
            log.warning("clip embedding failed", extra={"scan_id": scan["id"]}, exc_info=True)
    exclude = scan["listing_id"] if scan["source"] == "listing" else None
    candidates: list[Candidate] = []
    with pool.connection() as conn, conn.transaction():
        for (number, _), h, v in zip(images, hashes, vectors, strict=True):
            if scan["source"] == "listing":
                _fill_listing_image(conn, scan["listing_id"], number, h, v, clip_model_id if v else None)
            candidates += image_signal.find_candidates(conn, number, h, v, clip_model_id, exclude)
    return image_signal.best_matches(candidates)


def _fill_listing_image(
    conn: object,
    listing_id: int,
    sort_order: int,
    phash: int,
    clip: list[float] | None,
    clip_model_id: int | None,
) -> None:
    """Fill only what is missing, so a later model change never overwrites vectors already in use."""
    conn.execute(  # type: ignore[attr-defined]
        "UPDATE app.listing_images SET phash = %s "
        "WHERE listing_id = %s AND sort_order = %s AND phash IS NULL",
        (phash, listing_id, sort_order),
    )
    if clip is not None and clip_model_id is not None:
        conn.execute(  # type: ignore[attr-defined]
            """
            UPDATE app.listing_images SET clip_embedding = %s::vector, clip_model_id = %s
            WHERE listing_id = %s AND sort_order = %s AND clip_embedding IS NULL
            """,
            (image_signal.vector_literal(clip), clip_model_id, listing_id, sort_order),
        )


def _price(
    pool: ConnectionPool, scan: dict, analysis: TextAnalysis, listing: dict | None
) -> PriceSignal | None:
    with pool.connection() as conn:
        if listing is not None:
            if listing["listing_type"] != "room" or listing["monthly_rent_vnd"] is None:
                return None
            samples = price.samples_near_listing(conn, listing["id"], listing["is_self_contained"])
            return price.compute(listing["monthly_rent_vnd"], samples)

        claimed = conn.execute(
            "SELECT claimed_rent_vnd, claimed_ward_code FROM app.scans WHERE id = %s", (scan["id"],)
        ).fetchone()
        rent = claimed["claimed_rent_vnd"] or price.extract_rent(analysis.full_text)
        if rent is None or claimed["claimed_ward_code"] is None:
            return None  # quick checks compare within the ward the user picked; without it there is no sample
        return price.compute(rent, price.samples_in_ward(conn, claimed["claimed_ward_code"]))
