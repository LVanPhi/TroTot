"""Step 9: price signal, rooms only (docs/05-ai-worker.md; sample query in docs/03-database.md, Mẫu so giá).

A price far below the local median is a common bait. Fewer than 10 comparable rooms → NULL (no guessing).
"""

import re
import statistics
from collections.abc import Sequence
from dataclasses import dataclass

from troviec_worker.pipeline.injection import fold

MIN_SAMPLES = 10
MIN_TEXT_RENT = 300_000
MAX_TEXT_RENT = 20_000_000
SAMPLE_DAYS = 180
RADIUS_M = 1000

# Only text about renting a room is read for a price, so "lương 5 triệu/tháng" in a job post is ignored.
_ROOM_WORDS = re.compile(r"\b(thue|phong|tro|coc)\b")
_PREFER_WORDS = re.compile(r"(gia|thue|/\s*thang)")

# Patterns run on the folded (accent-free, lower-case) text, most specific first.
_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"(?<![\d.,])(\d+)\s*tr\s*(\d)(?!\d)"), "tr_digit"),  # 1tr8
    (re.compile(r"(?<![\d.,])(\d+(?:[.,]\d+)?)\s*(?:trieu|tr)\b"), "million"),  # 1,8 triệu / 2tr
    (re.compile(r"(?<![\d.,])(\d{1,3}(?:[.,]\d{3})+)\s*(?:d|vnd|vnđ|đ)?(?![\d])"), "grouped"),  # 1.800.000đ
    (re.compile(r"(?<![\d.,])(\d+)\s*k\b"), "thousand"),  # 1800k
]


@dataclass(frozen=True)
class PriceSignal:
    signal: float
    rent_vnd: int
    median_vnd: int
    sample_size: int


def _value(match: re.Match[str], kind: str) -> int:
    if kind == "tr_digit":
        return int(match.group(1)) * 1_000_000 + int(match.group(2)) * 100_000
    if kind == "million":
        return round(float(match.group(1).replace(",", ".")) * 1_000_000)
    if kind == "grouped":
        return int(re.sub(r"[.,]", "", match.group(1)))
    return int(match.group(1)) * 1_000


def extract_rent(text: str) -> int | None:
    """The monthly rent written in the text, or None. Prefers a number near "giá", "thuê" or "/tháng"."""
    folded = fold(text)
    if not _ROOM_WORDS.search(folded):
        return None
    found: list[tuple[int, int, int]] = []  # (not preferred, position, value)
    taken: list[tuple[int, int]] = []
    for pattern, kind in _PATTERNS:
        for m in pattern.finditer(folded):
            if any(m.start() < end and start < m.end() for start, end in taken):
                continue
            taken.append((m.start(), m.end()))
            value = _value(m, kind)
            if not MIN_TEXT_RENT <= value <= MAX_TEXT_RENT:
                continue
            window = folded[max(0, m.start() - 20) : m.end() + 15]
            found.append((0 if _PREFER_WORDS.search(window) else 1, m.start(), value))
    return min(found)[2] if found else None


def compute(rent_vnd: int, samples: Sequence[int]) -> PriceSignal | None:
    """MAD' = max(MAD, 0.05 × median); d = (median − price) / MAD'; signal = clip(d / 2.5, 0, 1)."""
    if len(samples) < MIN_SAMPLES:
        return None
    median = statistics.median(samples)
    mad = statistics.median(abs(s - median) for s in samples)
    mad = max(mad, 0.05 * median)
    d = (median - rent_vnd) / mad
    return PriceSignal(min(1.0, max(0.0, d / 2.5)), rent_vnd, round(median), len(samples))


SAMPLES_NEAR_POINT_SQL = """
SELECT r.monthly_rent_vnd
FROM app.listings l JOIN app.room_listings r ON r.listing_id = l.id
WHERE l.status IN ('verified', 'expired')
  AND l.updated_at > now() - make_interval(days => %(days)s)
  AND l.id <> %(exclude)s
  AND (%(self_contained)s::boolean IS NULL OR r.is_self_contained = %(self_contained)s)
  AND ST_DWithin(l.location, (SELECT location FROM app.listings WHERE id = %(exclude)s), %(radius)s)
"""

SAMPLES_IN_WARD_SQL = """
SELECT r.monthly_rent_vnd
FROM app.listings l JOIN app.room_listings r ON r.listing_id = l.id
WHERE l.status IN ('verified', 'expired')
  AND l.updated_at > now() - make_interval(days => %(days)s)
  AND l.ward_code = %(ward)s
"""


def samples_near_listing(conn: object, listing_id: int, self_contained: bool | None) -> list[int]:
    rows = conn.execute(  # type: ignore[attr-defined]
        SAMPLES_NEAR_POINT_SQL,
        {"days": SAMPLE_DAYS, "exclude": listing_id, "self_contained": self_contained, "radius": RADIUS_M},
    ).fetchall()
    return [row["monthly_rent_vnd"] for row in rows]


def samples_in_ward(conn: object, ward_code: str) -> list[int]:
    rows = conn.execute(SAMPLES_IN_WARD_SQL, {"days": SAMPLE_DAYS, "ward": ward_code}).fetchall()  # type: ignore[attr-defined]
    return [row["monthly_rent_vnd"] for row in rows]
