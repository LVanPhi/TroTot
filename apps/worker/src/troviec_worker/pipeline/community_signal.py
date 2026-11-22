"""Step 10: community signal (docs/05-ai-worker.md).  c = Σ confirmed_report_count over the scan's
identifiers; base = 1 − e^(−c/2) (1 report → 0.39, 3 → 0.78). Listing scans add 0.2 when the poster's
account is younger than 7 days. No identifier and not a listing → NULL.
"""

import math
from datetime import datetime, timedelta

NEW_ACCOUNT = timedelta(days=7)
NEW_ACCOUNT_BONUS = 0.2


def compute(
    confirmed_reports: int,
    identifier_count: int,
    source: str,
    poster_created_at: datetime | None,
    now: datetime,
) -> float | None:
    if identifier_count == 0 and source != "listing":
        return None
    value = 1 - math.exp(-confirmed_reports / 2)
    if source == "listing" and poster_created_at is not None and now - poster_created_at < NEW_ACCOUNT:
        value += NEW_ACCOUNT_BONUS
    return min(1.0, max(0.0, value))


def load_counts(conn: object, scan_id: int) -> tuple[int, int]:
    """(Σ confirmed reports, number of identifiers) for the scan."""
    row = conn.execute(  # type: ignore[attr-defined]
        """
        SELECT coalesce(sum(st.confirmed_report_count), 0)::int AS reports,
               count(si.identifier_id)::int AS ids
        FROM app.scan_identifiers si
        LEFT JOIN app.identifier_stats st ON st.identifier_id = si.identifier_id
        WHERE si.scan_id = %s
        """,
        (scan_id,),
    ).fetchone()
    return row["reports"], row["ids"]
