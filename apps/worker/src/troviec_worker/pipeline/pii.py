"""Step 5: mask personal data before anything leaves the worker (docs/05-ai-worker.md, docs/07-security.md).

Mandatory control before text is sent to an external LLM and before evidence is stored.
Names use NAME_RE (title + 1-4 capitalised words) and, when installed, underthesea NER (PER entities).
"""

import logging
import re
from collections.abc import Callable

import regex

from troviec_worker.pipeline.identifiers import DIGITS_RE, PHONE_RE, URL_RE, near_bank_keyword

log = logging.getLogger(__name__)

EMAIL_RE = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")
CCCD_RE = re.compile(r"(?<!\d)\d{12}(?!\d)")
CCCD_KEYWORD_RE = re.compile(r"cccd|căn\s*cước|can\s*cuoc|cmnd|chứng\s*minh", re.IGNORECASE)
NAME_RE = regex.compile(
    r"\b(?i:anh|chị|em|cô|chú|bác|ông|bà|bạn|chủ nhà|a|c)\s+(\p{Lu}\p{Ll}*(?:\s+\p{Lu}\p{Ll}*){0,3})"
)

NerFn = Callable[[str], list[tuple[int, int]]]


def mask(text: str, ner: NerFn | None = None) -> str:
    """Replace emails, links, ID numbers, bank accounts, phones and names with placeholders."""
    text = EMAIL_RE.sub("[EMAIL]", text)
    text = URL_RE.sub("[LINK]", text)
    text = _sub_near(text, CCCD_RE, CCCD_KEYWORD_RE, "[CCCD]")
    text = PHONE_RE.sub("[SĐT]", text)
    text = DIGITS_RE.sub(
        lambda m: "[STK]" if near_bank_keyword(m.string, m.start(), m.end()) else m.group(), text
    )
    text = NAME_RE.sub(lambda m: m.group()[: m.start(1) - m.start()] + "[TÊN]", text)
    for start, end in sorted(_ner_spans(text, ner), reverse=True):
        text = text[:start] + "[TÊN]" + text[end:]
    return text


def underthesea_people(text: str) -> list[tuple[int, int]]:
    """Character spans of PER entities from underthesea NER (optional dependency, `ml` extra)."""
    from underthesea import ner  # type: ignore[import-not-found]

    spans: list[tuple[int, int]] = []
    cursor = 0
    current: tuple[int, int] | None = None
    for word, _pos, _chunk, tag in ner(text):
        start = text.find(word, cursor)
        if start < 0:
            continue
        end = start + len(word)
        cursor = end
        if tag == "B-PER":
            if current:
                spans.append(current)
            current = (start, end)
        elif tag == "I-PER" and current:
            current = (current[0], end)
        elif current:
            spans.append(current)
            current = None
    if current:
        spans.append(current)
    return spans


def _ner_spans(text: str, ner: NerFn | None) -> list[tuple[int, int]]:
    if ner is None:
        return []
    try:
        return [(s, e) for s, e in ner(text) if "[" not in text[s:e]]
    except Exception:  # noqa: BLE001 - NER is a second layer; the regex layer already ran
        log.warning("ner failed; regex masking only", exc_info=True)
        return []


def _sub_near(text: str, pattern: re.Pattern[str], keyword: re.Pattern[str], placeholder: str) -> str:
    def replace(m: re.Match[str]) -> str:
        window = m.string[max(0, m.start() - 30) : m.end() + 30]
        return placeholder if keyword.search(window) else m.group()

    return pattern.sub(replace, text)
