"""Deterministic LLM stand-in for dev, CI and E2E (LLM_FAKE=1 + model_versions `llm fake/deterministic`).

Same input → same output. It flags what the rules find (with valid quotes, so evidence checks pass)
and returns a
high overall_risk when anything is flagged, so E2E scenarios can reach the "high" level.
"""

import re

from troviec_worker.llm.schema import LlmFlag, LlmResult
from troviec_worker.pipeline import injection, rules

_DATA = re.compile(r"<TIN[^>]*>\n(.*)\n</TIN>", re.DOTALL)


class FakeLlmClient:
    def analyse(self, system: str, user: str, criterion_codes: list[str], timeout: float) -> LlmResult:
        match = _DATA.search(user)
        text = match.group(1) if match else user
        flags = [
            LlmFlag(criterion_code=f.code, evidence_quote=f.evidence[:200])
            for f in rules.evaluate(text, set(criterion_codes))
        ]
        if "prompt_injection" in criterion_codes:
            flags += [
                LlmFlag(criterion_code="prompt_injection", evidence_quote=f.evidence[:200])
                for f in injection.detect(text)[:1]
            ]
        return LlmResult(flags=flags[:10], overall_risk=0.9 if flags else 0.1)
