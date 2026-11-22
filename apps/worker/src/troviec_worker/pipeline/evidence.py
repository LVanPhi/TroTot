"""Evidence quotes (docs/05-ai-worker.md, step 7): a flag counts only if its quote really is in the text.

Both sides are normalized (NFC, casefold, collapsed whitespace, punctuation trimmed at the ends); the stored
evidence is the matching span of the original masked text, so it keeps accents and original casing.
"""

import unicodedata

MIN_QUOTE = 6
MAX_EVIDENCE = 300
# Not [ or ]: masked placeholders such as [SĐT] and [TÊN] end with "]" and must stay in the evidence.
_EDGE_PUNCT = " \t\n.,;:!?\"'“”‘’(){}…-–—"


def _normalize_with_map(text: str) -> tuple[str, list[int]]:
    """Normalized text plus, for each normalized character, the index of the original character."""
    out: list[str] = []
    index: list[int] = []
    previous_space = False
    for i, ch in enumerate(unicodedata.normalize("NFC", text)):
        if ch.isspace():
            if previous_space:
                continue
            previous_space = True
            out.append(" ")
            index.append(i)
            continue
        previous_space = False
        for folded in ch.casefold():
            out.append(folded)
            index.append(i)
    return "".join(out), index


def find_quote(quote: str, masked_text: str) -> str | None:
    """The original span of `masked_text` that `quote` matches, or None when invalid (too short,
    not found)."""
    needle, _ = _normalize_with_map(quote)
    needle = needle.strip(_EDGE_PUNCT)
    if len(needle) < MIN_QUOTE:
        return None
    haystack, index = _normalize_with_map(masked_text)
    at = haystack.find(needle)
    if at < 0:
        return None
    nfc = unicodedata.normalize("NFC", masked_text)
    start, end = index[at], index[at + len(needle) - 1] + 1
    return nfc[start:end][:MAX_EVIDENCE]
