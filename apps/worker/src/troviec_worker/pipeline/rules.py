"""Fallback rules when no LLM runs (docs/05-ai-worker.md, step 7).

Regexes run on the accent-free, lower-case copy of the masked text; the evidence is the matching
sentence of the masked text with its accents. Only the 7 seeded criteria have rules (prompt_injection
comes from injection.py);
criteria an admin adds later are judged by the LLM only.
"""

import re
from dataclasses import dataclass

from troviec_worker.pipeline.injection import fold, sentence_around

RULES: dict[str, list[re.Pattern[str]]] = {
    code: [re.compile(p) for p in patterns]
    for code, patterns in {
        "upfront_fee": [
            r"phi (ho so|dong phuc|dao tao|kich hoat|giu cho lam|nhan viec)",
            r"(dat coc|tien coc|bao dam|ky quy) .{0,20}(nhan viec|lam viec|cong viec|giu viec)",
            r"(nop|dong|chuyen) (truoc )?\d+\s*(k|nghin|ngan|tr|trieu)\b.{0,20}(viec|ho so|tai khoan)",
        ],
        "deposit_before_viewing": [
            r"(chuyen|ck|gui|dat) (coc|tien coc|tien giu cho)",
            r"giu cho .{0,20}(chuyen|ck|coc)",
            r"(o xa|dang ban|khong o nha|chua ve).{0,40}(coc|chuyen khoan|ck)",
        ],
        "pressure": [
            r"(nhieu nguoi|rat nhieu ban|may ban) (dang )?(hoi|xem|lien he)",
            r"chi (con|con lai) (1|mot) (phong|suat|cho|slot)",
            r"(chuyen|chot|coc) (ngay|luon) (hom nay|trong hom nay|bay gio)",
            r"\bgap\b.{0,20}(coc|chuyen|chot)",
        ],
        "vague_high_pay": [
            r"(viec nhe|lam tai nha|chi can (dien thoai|dt)|chot don|like (bai|video)|tha tim)"
            r".{0,60}\d+\s*(k|tr|trieu|nghin)",
            r"thu nhap (cao|khung|hap dan).{0,30}\d+\s*(tr|trieu)",
            r"\d+\s*(tr|trieu)\s*/\s*(tuan|ngay)",
        ],
        "chat_only_no_address": [
            r"(chi|chi can) (nhan tin|inbox|ib|zalo|telegram)",
            r"phong van (qua|online qua) (tin nhan|zalo|telegram|messenger)",
            r"khong (can|phai) (den|gap) (truc tiep|van phong)",
        ],
        "requests_sensitive_info": [
            r"(gui|chup|cung cap) (anh )?(cccd|can cuoc|cmnd|ma otp|otp|mat khau)",
            r"(cung cap|gui) (so tai khoan|stk) .{0,20}(nhan luong|xac minh)",
            r"cai (dat )?(app|ung dung) .{0,30}(nhan viec|lam viec|xac minh)",
        ],
    }.items()
}


@dataclass(frozen=True)
class RuleFlag:
    code: str
    evidence: str


def evaluate(masked_text: str, active_codes: set[str]) -> list[RuleFlag]:
    """One flag per matching active criterion, with the first matching sentence as evidence."""
    folded = fold(masked_text)
    flags: list[RuleFlag] = []
    for code, patterns in RULES.items():
        if code not in active_codes:
            continue
        for pattern in patterns:
            m = pattern.search(folded)
            if m:
                flags.append(RuleFlag(code, sentence_around(masked_text, m.start(), m.end())))
                break
    return flags


def text_signal(flag_count: int) -> float:
    """min(1, 0.35 × number of matching criteria)."""
    return min(1.0, 0.35 * flag_count)
