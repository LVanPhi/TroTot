"""Step 8: image signal (docs/05-ai-worker.md; queries in docs/03-database.md, Ảnh gần trùng).  pHash finds
resized/recompressed copies; CLIP (when the image_embedding model is running) also finds crops and light
edits. A candidate is a match when Hamming ≤ 10 or cosine ≥ 0.92. Quick check images are compared against
listing photos but never added to them.
"""

import io
from collections.abc import Sequence
from dataclasses import dataclass

import imagehash
from PIL import Image

MAX_HAMMING = 10
MIN_COSINE = 0.92
MAX_MATCHES = 10
CANDIDATES_PER_QUERY = 20
BAD_STATUSES = ("hidden", "removed", "rejected")


def to_signed(value: int) -> int:
    """imagehash's unsigned 64-bit value → PostgreSQL BIGINT (two's complement)."""
    return value - 2**64 if value >= 2**63 else value


def to_unsigned(value: int) -> int:
    return value + 2**64 if value < 0 else value


def phash(image: bytes) -> int:
    """Signed 64-bit pHash, hash_size 8."""
    with Image.open(io.BytesIO(image)) as img:
        return to_signed(int(str(imagehash.phash(img.convert("RGB"), hash_size=8)), 16))


def hamming(a: int, b: int) -> int:
    return (to_unsigned(a) ^ to_unsigned(b)).bit_count()


@dataclass(frozen=True)
class Candidate:
    """A listing photo close to one input image, with both distances (the match table needs both)."""

    source_image_no: int
    listing_image_id: int
    listing_id: int
    listing_status: str
    poster_id: int
    hamming_distance: int
    clip_similarity: float

    @property
    def is_match(self) -> bool:
        return self.hamming_distance <= MAX_HAMMING or self.clip_similarity >= MIN_COSINE


def best_matches(candidates: Sequence[Candidate]) -> list[Candidate]:
    """Real matches, one row per listing photo (closest input image), best first, at most 10."""
    best: dict[int, Candidate] = {}
    for c in candidates:
        if not c.is_match:
            continue
        current = best.get(c.listing_image_id)
        if current is None or _rank(c) < _rank(current):
            best[c.listing_image_id] = c
    return sorted(best.values(), key=_rank)[:MAX_MATCHES]


def _rank(c: Candidate) -> tuple[int, float]:
    return (c.hamming_distance, -c.clip_similarity)


def signal(
    matches: Sequence[Candidate], image_count: int, source: str, poster_id: int | None
) -> float | None:
    """Highest value over the matches (step 8 table); NULL when there was no image."""
    if image_count == 0:
        return None
    value = 0.0
    for m in matches:
        if m.listing_status in BAD_STATUSES:
            value = max(value, 1.0)
        elif source == "quick_check":
            value = max(value, 0.5)
        elif m.poster_id != poster_id:
            value = max(value, 0.8)
        # listing scan matching another listing of the same poster (one owner, many rooms): 0
    return value


def vector_literal(vector: Sequence[float]) -> str:
    """pgvector text form, passed as a parameter and cast with ::vector."""
    return "[" + ",".join(f"{v:.7g}" for v in vector) + "]"


CANDIDATES_SQL = """
WITH cand AS (
    (SELECT li.id FROM app.listing_images li
     WHERE li.phash IS NOT NULL AND li.listing_id <> coalesce(%(exclude)s, -1)
       AND bit_count((li.phash # %(phash)s)::bit(64)) <= %(max_hamming)s
     ORDER BY bit_count((li.phash # %(phash)s)::bit(64))
     LIMIT %(limit)s)
    UNION
    (SELECT li.id FROM app.listing_images li
     WHERE %(clip)s::text IS NOT NULL AND li.clip_embedding IS NOT NULL AND li.clip_model_id = %(clip_model)s
       AND li.listing_id <> coalesce(%(exclude)s, -1)
     ORDER BY li.clip_embedding <=> %(clip)s::vector
     LIMIT %(limit)s)
)
SELECT li.id AS listing_image_id, li.listing_id, l.status::text AS listing_status, l.poster_id,
       coalesce(bit_count((li.phash # %(phash)s)::bit(64)), 64)::int AS hamming_distance,
       CASE WHEN %(clip)s::text IS NOT NULL AND li.clip_model_id = %(clip_model)s
            THEN (1 - (li.clip_embedding <=> %(clip)s::vector))::real ELSE 0 END AS clip_similarity
FROM cand JOIN app.listing_images li ON li.id = cand.id JOIN app.listings l ON l.id = li.listing_id
"""


def find_candidates(
    conn: object,
    source_image_no: int,
    image_phash: int,
    clip: Sequence[float] | None,
    clip_model_id: int | None,
    exclude_listing_id: int | None,
) -> list[Candidate]:
    rows = conn.execute(  # type: ignore[attr-defined]
        CANDIDATES_SQL,
        {
            "phash": image_phash,
            "clip": vector_literal(clip) if clip is not None else None,
            "clip_model": clip_model_id,
            "exclude": exclude_listing_id,
            "max_hamming": MAX_HAMMING,
            "limit": CANDIDATES_PER_QUERY,
        },
    ).fetchall()
    return [Candidate(source_image_no=source_image_no, **row) for row in rows]
