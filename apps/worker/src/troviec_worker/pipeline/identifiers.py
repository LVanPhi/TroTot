"""Step 4: extract phone numbers, bank accounts and links; store only HMAC + hint (docs/05-ai-worker.md).

Runs on the unmasked text. Original values are never logged or stored.
"""

import base64
import hashlib
import hmac
import re
from dataclasses import dataclass
from typing import Literal
from urllib.parse import parse_qsl, urlencode, urlsplit

Kind = Literal["phone", "bank_account", "url"]

PHONE_RE = re.compile(r"(?<!\d)(?:\+?84|0)[\s.\-]?[35789](?:[\s.\-]?\d){8}(?!\d)")

_BANK_KEYWORDS = (
    r"stk|số\s*tài\s*khoản|so\s*tai\s*khoan|\btk\b|ngân\s*hàng|ngan\s*hang|vcb|vietcombank|\bmb\b|mbbank|techcombank|"
    r"tcb|bidv|vietinbank|agribank|tpbank|\bacb\b|vpbank|sacombank|momo"
)
BANK_KEYWORD_RE = re.compile(_BANK_KEYWORDS, re.IGNORECASE)
DIGITS_RE = re.compile(r"(?<![\d.,])\d(?:[ ]?\d){5,18}(?![\d.,])")
KEYWORD_WINDOW = 30

_SHORT_HOSTS = r"zalo\.me|bit\.ly|t\.me|m\.me|facebook\.com|fb\.com|fb\.me|tinyurl\.com|shorturl\.at"
URL_RE = re.compile(
    rf"https?://[^\s<>\"']+|(?<![\w.@])(?:www\.)?(?:{_SHORT_HOSTS})/[^\s<>\"']*", re.IGNORECASE
)
_TRAILING_PUNCT = ".,;:!?)]}…"


@dataclass(frozen=True)
class Identifier:
    kind: Kind
    normalized: str
    hint: str
    start: int
    end: int


def normalize_phone(raw: str) -> str:
    digits = re.sub(r"\D", "", raw)
    if digits.startswith("84"):
        digits = digits[2:]
    elif digits.startswith("0"):
        digits = digits[1:]
    return "+84" + digits


def normalize_url(raw: str) -> tuple[str, str]:
    """(normalized, host): lower-case host, no scheme, no utm_* parameters, no trailing slash."""
    raw = raw.rstrip(_TRAILING_PUNCT)
    parts = urlsplit(raw if "://" in raw else "https://" + raw)
    host = (parts.hostname or "").lower().removeprefix("www.")
    query = urlencode([(k, v) for k, v in parse_qsl(parts.query) if not k.lower().startswith("utm_")])
    path = parts.path.rstrip("/")
    return host + path + (f"?{query}" if query else ""), host


def extract(text: str) -> list[Identifier]:
    """Unique identifiers in order of appearance."""
    found: list[Identifier] = []
    taken: list[tuple[int, int]] = []

    for m in URL_RE.finditer(text):
        normalized, host = normalize_url(m.group())
        end = m.start() + len(m.group().rstrip(_TRAILING_PUNCT))
        found.append(Identifier("url", normalized, host, m.start(), end))
        taken.append((m.start(), end))

    for m in PHONE_RE.finditer(text):
        if _overlaps(m.start(), m.end(), taken):
            continue
        normalized = normalize_phone(m.group())
        found.append(Identifier("phone", normalized, "•••" + normalized[-3:], m.start(), m.end()))
        taken.append((m.start(), m.end()))

    for m in DIGITS_RE.finditer(text):
        if _overlaps(m.start(), m.end(), taken) or not near_bank_keyword(text, m.start(), m.end()):
            continue
        digits = re.sub(r"\D", "", m.group())
        found.append(Identifier("bank_account", digits, "•••" + digits[-4:], m.start(), m.end()))
        taken.append((m.start(), m.end()))

    unique: dict[tuple[str, str], Identifier] = {}
    for identifier in sorted(found, key=lambda i: i.start):
        unique.setdefault((identifier.kind, identifier.normalized), identifier)
    return list(unique.values())


def decode_key(identifier_hmac_key: str) -> bytes:
    """The HMAC key is the base64-decoded bytes of IDENTIFIER_HMAC_KEY (shared with the API), not the text."""
    return base64.b64decode(identifier_hmac_key, validate=True)


def value_hmac(key: bytes, kind: Kind, normalized: str) -> bytes:
    """HMAC-SHA256(key, f"{kind}:{normalized}") — same input string as the API's IdentifierHasher."""
    return hmac.new(key, f"{kind}:{normalized}".encode(), hashlib.sha256).digest()


def near_bank_keyword(text: str, start: int, end: int) -> bool:
    window = text[max(0, start - KEYWORD_WINDOW) : min(len(text), end + KEYWORD_WINDOW)]
    return BANK_KEYWORD_RE.search(window) is not None


def _overlaps(start: int, end: int, spans: list[tuple[int, int]]) -> bool:
    return any(start < s_end and s_start < end for s_start, s_end in spans)
