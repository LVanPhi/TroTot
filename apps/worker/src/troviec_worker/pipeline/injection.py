"""Step 6: detect prompt-injection sentences on the masked text (docs/05-ai-worker.md).

Matching runs on an accent-free, lower-case copy with the same length, so positions map back to the
masked text
that still has its accents; the evidence is that original sentence, cut to at most 300 characters.
"""

import re
import unicodedata
from dataclasses import dataclass

MAX_EVIDENCE = 300

PATTERNS = [
    re.compile(p)
    for p in (
        r"bo qua (moi|cac|tat ca|nhung) (huong dan|chi dan|chi thi|lenh)",
        r"\bignore (all|previous|the above|any)\b",
        r"\b(he thong|ai|chatbot|mo hinh|tro ly)\s+(hay|phai)\s+(cham|danh gia|tra loi|cho diem|xep)",
        r"cham (tin|bai) nay (an toan|thap|la 0|0 diem|0\b)",
        r"system prompt",
        r"\byou are now\b",
        r"\bdisregard (all|previous|the above)\b",
    )
]
_SENTENCE_END = re.compile(r"[.!?\n]")


@dataclass(frozen=True)
class InjectionFinding:
    evidence: str


def fold(text: str) -> str:
    """Lower-case and strip Vietnamese accents character by character (same length as the input)."""
    out = []
    for ch in text:
        base = unicodedata.normalize("NFD", ch)[0]
        out.append("d" if base in "đĐ" else base.lower())
    return "".join(out)


def detect(masked_text: str) -> list[InjectionFinding]:
    folded = fold(masked_text)
    findings: list[InjectionFinding] = []
    seen: set[str] = set()
    for pattern in PATTERNS:
        for m in pattern.finditer(folded):
            evidence = _sentence_around(masked_text, m.start(), m.end())
            if evidence not in seen:
                seen.add(evidence)
                findings.append(InjectionFinding(evidence))
    return findings


def _sentence_around(text: str, start: int, end: int) -> str:
    left = max((m.end() for m in _SENTENCE_END.finditer(text, 0, start)), default=0)
    right_match = _SENTENCE_END.search(text, end)
    right = right_match.end() if right_match else len(text)
    sentence = text[left:right].strip()
    if len(sentence) <= MAX_EVIDENCE:
        return sentence
    # Keep the matched part, centred, within 300 characters.
    centre = (start + end) // 2 - left
    begin = max(0, min(centre - MAX_EVIDENCE // 2, len(sentence) - MAX_EVIDENCE))
    return sentence[begin : begin + MAX_EVIDENCE].strip()
