"""LLM output contract (docs/05-ai-worker.md, step 7), validated with pydantic whatever the
provider returns."""

from typing import Any

from pydantic import BaseModel, ConfigDict, Field


class LlmFlag(BaseModel):
    model_config = ConfigDict(extra="forbid")
    criterion_code: str
    evidence_quote: str = Field(max_length=300)


class LlmResult(BaseModel):
    model_config = ConfigDict(extra="forbid")
    flags: list[LlmFlag] = Field(max_length=10)
    overall_risk: float = Field(ge=0, le=1)


def json_schema(criterion_codes: list[str]) -> dict[str, Any]:
    """Strict JSON schema sent as response_format; criterion_code limited to the active codes."""
    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["flags", "overall_risk"],
        "properties": {
            "flags": {
                "type": "array",
                "maxItems": 10,
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["criterion_code", "evidence_quote"],
                    "properties": {
                        "criterion_code": {"type": "string", "enum": criterion_codes},
                        "evidence_quote": {"type": "string", "maxLength": 300},
                    },
                },
            },
            "overall_risk": {"type": "number", "minimum": 0, "maximum": 1},
        },
    }
