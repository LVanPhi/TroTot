"""Step 7: text signal from the LLM when one is usable, otherwise from the rules (docs/05-ai-worker.md)."""

import logging
from dataclasses import dataclass, field

from troviec_worker.llm import prompt
from troviec_worker.llm.base import LlmClient, LlmUnavailableError
from troviec_worker.pipeline import rules
from troviec_worker.pipeline.evidence import find_quote
from troviec_worker.pipeline.injection import InjectionFinding

log = logging.getLogger(__name__)

INJECTION_CODE = "prompt_injection"
NO_EVIDENCE_CAP = 0.3


@dataclass
class TextOutcome:
    signal: float | None
    flags: list[tuple[str, str]] = field(default_factory=list)  # (criterion code, evidence), one per code
    llm_used: bool = False


def compute(
    masked_text: str,
    injections: list[InjectionFinding],
    criteria: list[prompt.Criterion],
    llm: LlmClient | None,
    llm_timeout: float,
    kind: str = "unknown",
) -> TextOutcome:
    if not masked_text.strip():
        return TextOutcome(signal=None)

    outcome = _from_llm(masked_text, criteria, llm, llm_timeout, kind) if llm is not None else None
    if outcome is None:
        outcome = _from_rules(masked_text, criteria)
    _add_injection(outcome, injections)
    if not outcome.llm_used:
        outcome.signal = rules.text_signal(len(outcome.flags))
    return outcome


def _from_llm(
    masked_text: str, criteria: list[prompt.Criterion], llm: LlmClient, timeout: float, kind: str
) -> TextOutcome | None:
    codes = [c.code for c in criteria]
    try:
        result = llm.analyse(
            prompt.system_prompt(criteria), prompt.user_message(masked_text, kind), codes, timeout
        )
    except LlmUnavailableError as error:
        log.warning("llm unavailable, using rules", extra={"reason": str(error)})
        return None

    flags: dict[str, str] = {}
    for flag in result.flags:
        if flag.criterion_code not in codes or flag.criterion_code in flags:
            continue
        evidence = find_quote(flag.evidence_quote, masked_text)
        if evidence is not None:  # quotes not found verbatim are dropped (no evidence, no flag)
            flags[flag.criterion_code] = evidence
    # Without one valid flag, a text-only score stays low (principle 3: no high score without evidence).
    signal = result.overall_risk if flags else min(result.overall_risk, NO_EVIDENCE_CAP)
    return TextOutcome(signal=signal, flags=list(flags.items()), llm_used=True)


def _from_rules(masked_text: str, criteria: list[prompt.Criterion]) -> TextOutcome:
    found = rules.evaluate(masked_text, {c.code for c in criteria})
    return TextOutcome(signal=None, flags=[(f.code, f.evidence) for f in found])


def _add_injection(outcome: TextOutcome, injections: list[InjectionFinding]) -> None:
    """The injection rule always runs, even if admins stopped giving the criterion to the LLM."""
    if injections and all(code != INJECTION_CODE for code, _ in outcome.flags):
        outcome.flags.append((INJECTION_CODE, injections[0].evidence))
