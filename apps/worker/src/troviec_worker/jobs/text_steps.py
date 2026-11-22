"""Steps 1-6 of a scan (docs/05-ai-worker.md): load, OCR, normalize, identifiers, PII masking, injection.

Results that later steps need (masked text, injection findings) are returned; what must be kept is written:
scan_images.ocr_text, identifiers and scan_identifiers. Logs carry ids and counts only, never text or values.
"""

import logging
from dataclasses import dataclass, field

from psycopg_pool import ConnectionPool

from troviec_worker.pipeline import identifiers, injection, pii
from troviec_worker.pipeline.budget import Budget
from troviec_worker.pipeline.normalize import full_text
from troviec_worker.pipeline.ocr import OcrEngine, ocr_images, tesseract
from troviec_worker.storage import ImageLoader

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class PipelineDeps:
    load_image: ImageLoader
    hmac_key: bytes
    scan_bucket: str = "scan-images"
    ocr_engine: OcrEngine = tesseract
    ner: pii.NerFn | None = None


@dataclass
class TextAnalysis:
    masked_text: str
    injections: list[injection.InjectionFinding] = field(default_factory=list)
    identifier_count: int = 0
    ocr_texts: list[str | None] = field(default_factory=list)


def mark_processing(pool: ConnectionPool, scan_id: int) -> None:
    with pool.connection() as conn:
        conn.execute(
            "UPDATE app.scans SET status = 'processing' WHERE id = %s AND status = 'queued'", (scan_id,)
        )


def analyse(pool: ConnectionPool, scan: dict, deps: PipelineDeps, budget: Budget) -> TextAnalysis:
    scan_id = scan["id"]
    with pool.connection() as conn:
        input_text = conn.execute("SELECT input_text FROM app.scans WHERE id = %s", (scan_id,)).fetchone()[
            "input_text"
        ]
        images = conn.execute(
            "SELECT image_no, object_key FROM app.scan_images WHERE scan_id = %s ORDER BY image_no",
            (scan_id,),
        ).fetchall()

    # Step 2: OCR only for quick checks (listing photos are rooms, not screenshots).
    ocr_texts: list[str | None] = []
    if scan["source"] == "quick_check" and images:
        blobs = [_load(deps, image["object_key"], scan_id) for image in images]
        ocr_texts = ocr_images([b for b in blobs if b is not None], budget, deps.ocr_engine)
        ocr_texts = _align(blobs, ocr_texts)
        _save_ocr(pool, scan_id, images, ocr_texts)

    text = full_text(input_text, ocr_texts)  # step 3
    found = identifiers.extract(text)  # step 4, on unmasked text
    _save_identifiers(pool, scan_id, found, deps.hmac_key)
    masked = pii.mask(text, deps.ner)  # step 5
    findings = injection.detect(masked)  # step 6
    log.info(
        "text steps done",
        extra={
            "scan_id": scan_id,
            "images": len(images),
            "ocr_ok": sum(t is not None for t in ocr_texts),
            "identifiers": len(found),
            "injections": len(findings),
        },
    )
    return TextAnalysis(masked, findings, len(found), ocr_texts)


def _load(deps: PipelineDeps, key: str, scan_id: int) -> bytes | None:
    try:
        return deps.load_image(deps.scan_bucket, key)
    except Exception:  # noqa: BLE001 - a missing object loses that image only; redelivery retries the scan
        log.warning("image load failed", extra={"scan_id": scan_id}, exc_info=True)
        return None


def _align(blobs: list[bytes | None], texts: list[str | None]) -> list[str | None]:
    """OCR ran only on loaded images; put None back where an image could not be loaded."""
    it = iter(texts)
    return [next(it) if blob is not None else None for blob in blobs]


def _save_ocr(pool: ConnectionPool, scan_id: int, images: list[dict], texts: list[str | None]) -> None:
    with pool.connection() as conn, conn.transaction():
        for image, text in zip(images, texts, strict=True):
            conn.execute(
                "UPDATE app.scan_images SET ocr_text = %s WHERE scan_id = %s AND image_no = %s",
                (text[:10_000] if text else None, scan_id, image["image_no"]),
            )


def _save_identifiers(
    pool: ConnectionPool, scan_id: int, found: list[identifiers.Identifier], key: bytes
) -> None:
    """docs/03-database.md, Định danh (worker): upsert identifiers, link them to the scan (idempotent)."""
    with pool.connection() as conn, conn.transaction():
        for item in found:
            digest = identifiers.value_hmac(key, item.kind, item.normalized)
            conn.execute(
                """
                INSERT INTO app.identifiers (kind, value_hmac, hint) VALUES (%s::app.identifier_kind, %s, %s)
                ON CONFLICT (kind, value_hmac) DO NOTHING
                """,
                (item.kind, digest, item.hint),
            )
            row = conn.execute(
                "SELECT id FROM app.identifiers WHERE kind = %s::app.identifier_kind AND value_hmac = %s",
                (item.kind, digest),
            ).fetchone()
            conn.execute(
                """
                INSERT INTO app.scan_identifiers (scan_id, identifier_id) VALUES (%s, %s)
                ON CONFLICT DO NOTHING
                """,
                (scan_id, row["id"]),
            )
