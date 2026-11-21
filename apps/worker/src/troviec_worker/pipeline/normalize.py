"""Step 3: normalize text (docs/05-ai-worker.md)."""

import re
import unicodedata

MAX_CHARS = 10_000

_SPACES = re.compile(r"[^\S\n]+")  # whitespace except newlines
_BLANK_LINES = re.compile(r"\n\s*\n+")


def normalize(text: str) -> str:
    """Unicode NFC, runs of spaces/tabs -> one space, keep newlines, trim, cut to 10,000 characters."""
    text = unicodedata.normalize("NFC", text)
    text = _SPACES.sub(" ", text)
    text = "\n".join(line.strip() for line in text.split("\n"))
    text = _BLANK_LINES.sub("\n", text).strip()
    return text[:MAX_CHARS]


def full_text(input_text: str | None, ocr_texts: list[str | None]) -> str:
    """input_text + "\\n" + OCR text of each image, normalized."""
    parts = [input_text or ""] + [t for t in ocr_texts if t]
    return normalize("\n".join(parts))
