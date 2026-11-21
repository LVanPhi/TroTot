"""OpenAI-compatible chat completions (e.g. Groq): one call, temperature 0, strict JSON schema, no retry."""

import json
import logging

import httpx
from pydantic import ValidationError

from troviec_worker.llm.base import LlmUnavailableError
from troviec_worker.llm.schema import LlmResult, json_schema

log = logging.getLogger(__name__)

MAX_TOKENS = 1500


class OpenAiCompatClient:
    def __init__(
        self, base_url: str, api_key: str, model: str, transport: httpx.BaseTransport | None = None
    ) -> None:
        self._url = base_url.rstrip("/") + "/chat/completions"
        self._headers = {"Authorization": f"Bearer {api_key}"}
        self._model = model
        self._transport = transport

    def analyse(self, system: str, user: str, criterion_codes: list[str], timeout: float) -> LlmResult:
        if timeout <= 0:
            raise LlmUnavailableError("no time left")
        body = {
            "model": self._model,
            "temperature": 0,
            "max_tokens": MAX_TOKENS,
            "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "response_format": {
                "type": "json_schema",
                "json_schema": {"name": "risk_flags", "strict": True, "schema": json_schema(criterion_codes)},
            },
        }
        try:
            with httpx.Client(timeout=timeout, transport=self._transport) as client:
                response = client.post(self._url, headers=self._headers, json=body)
        except httpx.HTTPError as error:
            raise LlmUnavailableError(type(error).__name__) from error
        if response.status_code != 200:
            # 429 = free-tier quota used up; any non-200 falls back to rules. The body is not logged.
            raise LlmUnavailableError(f"http {response.status_code}")
        try:
            content = response.json()["choices"][0]["message"]["content"]
            return LlmResult.model_validate(json.loads(content))
        except (KeyError, IndexError, TypeError, ValueError, ValidationError) as error:
            raise LlmUnavailableError("invalid response") from error
