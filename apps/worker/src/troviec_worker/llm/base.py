"""LLM client interface. Any failure (error, timeout, 429, bad schema) means: fall back to rules."""

from typing import Protocol

from troviec_worker.llm.schema import LlmResult


class LlmUnavailableError(Exception):
    """The LLM could not give a valid answer in time; the caller uses the rules instead."""


class LlmClient(Protocol):
    def analyse(self, system: str, user: str, criterion_codes: list[str], timeout: float) -> LlmResult: ...
