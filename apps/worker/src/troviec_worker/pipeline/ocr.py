"""Step 2: OCR of quick-check images with Tesseract `vie` (docs/05-ai-worker.md).

A failing or slow image only loses its own text; the scan continues. Logs carry ids, never the text.
"""

import io
import logging
from collections.abc import Callable
from concurrent.futures import ThreadPoolExecutor

from PIL import Image, ImageOps

from troviec_worker.pipeline.budget import Budget

log = logging.getLogger(__name__)

TESSERACT_CONFIG = "--oem 1 --psm 6"
MIN_WIDTH = 1200
MAX_PARALLEL = 2

OcrEngine = Callable[[Image.Image, float], str]


def preprocess(image_bytes: bytes) -> Image.Image:
    """EXIF orientation, grayscale, upscale to 1,200 px wide when smaller, autocontrast."""
    with Image.open(io.BytesIO(image_bytes)) as opened:
        image = ImageOps.exif_transpose(opened).convert("L")
    if image.width < MIN_WIDTH:
        height = round(image.height * MIN_WIDTH / image.width)
        image = image.resize((MIN_WIDTH, height), Image.Resampling.LANCZOS)
    return ImageOps.autocontrast(image)


def tesseract(image: Image.Image, timeout: float) -> str:
    import pytesseract

    return pytesseract.image_to_string(image, lang="vie", config=TESSERACT_CONFIG, timeout=timeout)


def ocr_one(image_bytes: bytes, timeout: float, engine: OcrEngine = tesseract) -> str | None:
    """Text of one image, or None when unreadable, empty, out of time or failing."""
    if timeout <= 0:
        return None
    try:
        text = engine(preprocess(image_bytes), timeout).strip()
    except Exception:  # noqa: BLE001 - any OCR/decoding failure only drops this image's text
        log.warning("ocr failed for one image", exc_info=True)
        return None
    return text or None


def ocr_images(images: list[bytes], budget: Budget, engine: OcrEngine = tesseract) -> list[str | None]:
    """OCR in order, at most 2 at a time; each image gets min(10 s, remaining - 3 s) when it starts."""
    if not images:
        return []
    with ThreadPoolExecutor(max_workers=MAX_PARALLEL) as pool:
        futures = [pool.submit(lambda b: ocr_one(b, budget.ocr_timeout(), engine), image) for image in images]
        return [future.result() for future in futures]
